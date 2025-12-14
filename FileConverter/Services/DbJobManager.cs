using FileConverter.Data;
using FileConverter.Models;
using FileConverter.Services.Interfaces;
using FileConverter.Helpers;
// using Hangfire; // Удаляем зависимость от Hangfire Client
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration; // Добавляем для Configuration
using Microsoft.Extensions.Caching.Memory;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Threading.Channels; // Добавляем для ChannelClosedException

namespace FileConverter.Services
{
    public class DbJobManager : IJobManager
    {
        private readonly IJobRepository _repository;
        private readonly IMediaItemRepository _mediaItemRepository;
        // private readonly IBackgroundJobClient _backgroundJobClient; // Удаляем Hangfire Client
        private readonly IConversionLogger _conversionLogger; // Добавляем логгер конверсий
        private readonly ILogger<DbJobManager> _logger;
        private readonly IConfiguration _configuration;
        private readonly IVideoConverter _videoConverter;
        private readonly UrlValidator _urlValidator;
        private readonly IMemoryCache _memoryCache;
        
        public DbJobManager(
            IJobRepository repository,
            IMediaItemRepository mediaItemRepository,
            // IBackgroundJobClient backgroundJobClient, // Удаляем Hangfire Client
            IConversionLogger conversionLogger, // Добавляем логгер
            ILogger<DbJobManager> logger,
            IConfiguration configuration,
            IVideoConverter videoConverter,
            UrlValidator urlValidator,
            IMemoryCache memoryCache)
        {
            _repository = repository;
            _mediaItemRepository = mediaItemRepository;
            // _backgroundJobClient = backgroundJobClient; // Удаляем Hangfire Client
            _conversionLogger = conversionLogger; // Сохраняем логгер
            _logger = logger;
            _configuration = configuration;
            _videoConverter = videoConverter;
            _urlValidator = urlValidator;
            _memoryCache = memoryCache;
        }

        public async Task<ConversionJobResponse> EnqueueConversionJob(string videoUrl)
        {
            string? jobId = null; // Инициализируем jobId как null
            try
            {
                if (string.IsNullOrEmpty(videoUrl))
                {
                    throw new ArgumentException("URL видео не может быть пустым");
                }
                
                var newJob = new ConversionJob
                {
                    VideoUrl = videoUrl,
                    Status = ConversionStatus.Pending,
                    CreatedAt = DateTime.UtcNow, // Устанавливаем время создания
                    LastAttemptAt = DateTime.UtcNow // Устанавливаем время последней активности
                };
                jobId = newJob.Id; // Сохраняем ID для логирования ошибок
                
                await _repository.CreateJobAsync(newJob);
                await _conversionLogger.LogJobCreatedAsync(newJob.Id, newJob.VideoUrl); // Логируем создание задачи
                
                // Запускаем обработку через VideoConverter (с проверкой кэша и валидацией)
                try
                {
                    // Передаем задачу в VideoConverter для интеллектуальной обработки
                    await _videoConverter.ProcessVideo(newJob.Id);
                    _logger.LogInformation("Задача {JobId} передана в VideoConverter для обработки.", newJob.Id);
                    await _conversionLogger.LogSystemInfoAsync($"Задача {newJob.Id} передана в VideoConverter для обработки.");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Не удалось обработать задачу {JobId} через VideoConverter.", newJob.Id);
                    await _conversionLogger.LogErrorAsync(newJob.Id, $"Ошибка при обработке через VideoConverter: {ex.Message}", ex.StackTrace);
                    throw new InvalidOperationException($"Не удалось инициировать обработку задачи {newJob.Id}.", ex);
                }
                
                // Формируем URL для проверки статуса
                string apiBaseUrl = _configuration["AppSettings:BaseUrl"] ?? "http://localhost:5080"; // Используем значение по умолчанию, если не найдено
                
                return new ConversionJobResponse
                {
                    JobId = newJob.Id,
                    StatusUrl = $"{apiBaseUrl}/api/videoconverter/status/{newJob.Id}"
                };
            }
            catch (Exception ex)
            {
                 _logger.LogError(ex, "Ошибка при создании и постановке в очередь задачи {JobId} для URL: {VideoUrl}", jobId ?? "N/A", videoUrl);
                 // Если у нас есть ID задачи, и она была создана в БД, но не добавлена в канал, меняем статус на Failed
                 if(jobId != null) 
                 { 
                    try 
                    { 
                         await DbJobManager.UpdateJobStatusAsync(_repository, jobId, ConversionStatus.Failed, 
                            errorMessage: $"Ошибка при постановке в очередь: {ex.Message}");
                        await _conversionLogger.LogErrorAsync(jobId, $"Ошибка при постановке в очередь: {ex.Message}", ex.StackTrace, ConversionStatus.Failed);
                     } 
                     catch(Exception updateEx) 
                     {
                          _logger.LogError(updateEx, "Не удалось обновить статус задачи {JobId} на Failed после ошибки постановки в очередь.", jobId);
                     }
                 }
                 // Перевыбрасываем оригинальное или новое исключение
                throw new Exception($"Ошибка при создании задачи конвертации: {ex.Message}", ex); 
            }
        }

        public async Task<BatchJobResult> EnqueueBatchJobs(List<string> videoUrls)
        {
            var batchJob = new BatchJob { CreatedAt = DateTime.UtcNow };
            await _repository.CreateBatchJobAsync(batchJob);
            _logger.LogInformation("Создан пакет задач {BatchId}", batchJob.Id);

            var jobResponses = new List<ConversionJobResponse>();
            var createdJobs = new List<(string JobId, string VideoUrl)>(); // Сохраняем ID и URL для добавления в канал
            string apiBaseUrl = _configuration["AppSettings:BaseUrl"] ?? "http://localhost:5080";

            // Шаг 1: Создаем все задачи в БД
            foreach (var videoUrl in videoUrls)
            {
                if (string.IsNullOrEmpty(videoUrl))
                {
                    _logger.LogWarning("Пропущен пустой URL в пакете {BatchId}", batchJob.Id);
                    continue; // Пропускаем пустые URL
                }

                // Валидируем URL на раннем этапе
                if (!_urlValidator.IsUrlValid(videoUrl))
                {
                    _logger.LogWarning("Пропущен небезопасный URL {VideoUrl} в пакете {BatchId}", videoUrl, batchJob.Id);
                    continue; // Пропускаем небезопасные URL
                }

                ConversionJob? job = null;
                try 
                { 
                    job = new ConversionJob 
                    { 
                        VideoUrl = videoUrl,
                        Status = ConversionStatus.Pending,
                        BatchId = batchJob.Id,
                        CreatedAt = DateTime.UtcNow,
                        LastAttemptAt = DateTime.UtcNow
                    };
                        
                    await _repository.CreateJobAsync(job);
                    await _conversionLogger.LogJobCreatedAsync(job.Id, job.VideoUrl, job.BatchId); // Логируем создание
                    createdJobs.Add((job.Id, job.VideoUrl)); // Добавляем в список для последующей постановки в очередь

                    var response = new ConversionJobResponse
                    {
                        JobId = job.Id,
                        StatusUrl = $"{apiBaseUrl}/api/videoconverter/status/{job.Id}"
                    };
                    jobResponses.Add(response);
                }
                catch (Exception ex)
                {
                    var jobIdForLog = job?.Id ?? "не создан";
                    _logger.LogError(ex, "Ошибка при создании задачи {JobId} для URL {VideoUrl} в пакете {BatchId}", jobIdForLog, videoUrl, batchJob.Id);
                    
                    // Логируем ошибку только если задача была создана (имеет ID)
                    if (job != null && !string.IsNullOrEmpty(job.Id)) 
                    {
                        try
                        {
                            await _conversionLogger.LogErrorAsync(job.Id, $"Ошибка при создании задачи в пакете {batchJob.Id}: {ex.Message}", ex.StackTrace, ConversionStatus.Failed);
                        }
                        catch (Exception logEx)
                        {
                            _logger.LogError(logEx, "Не удалось записать ошибку в ConversionLogger для задачи {JobId}", job.Id);
                        }
                    }
                    // Не прерываем весь пакет, но логируем ошибку
                }
            }

            _logger.LogInformation("Создано {CreatedCount} задач для пакета {BatchId}. Добавление в очередь...", createdJobs.Count, batchJob.Id);

            // Шаг 2: Ставим созданные задачи в обработку через VideoConverter
            int queuedCount = 0;
            foreach(var jobInfo in createdJobs)
            {
                 try 
                 { 
                     // Передаем задачу в VideoConverter для интеллектуальной обработки
                     await _videoConverter.ProcessVideo(jobInfo.JobId);
                     _logger.LogInformation("Задача {JobId} из пакета {BatchId} передана в VideoConverter для обработки.", jobInfo.JobId, batchJob.Id);
                     await _conversionLogger.LogSystemInfoAsync($"Задача {jobInfo.JobId} (пакет {batchJob.Id}) передана в VideoConverter для обработки.");
                     queuedCount++;
                 }
                 catch (Exception ex)
                 {
                     _logger.LogError(ex, "Ошибка при обработке задачи {JobId} из пакета {BatchId} через VideoConverter.", jobInfo.JobId, batchJob.Id);
                      await _conversionLogger.LogErrorAsync(jobInfo.JobId, $"Ошибка при обработке через VideoConverter: {ex.Message}", ex.StackTrace);
                      // Обновляем статус на Failed, так как задача создана, но не удалось инициировать обработку
                      await DbJobManager.UpdateJobStatusAsync(_repository, jobInfo.JobId, ConversionStatus.Failed, errorMessage: $"Ошибка инициации обработки: {ex.Message}");
                 }
            }
             _logger.LogInformation("{QueuedCount} из {CreatedCount} задач пакета {BatchId} успешно переданы в VideoConverter для обработки.", queuedCount, createdJobs.Count, batchJob.Id);

            // Возвращаем результат с ID пакета
            return new BatchJobResult
            {
                BatchId = batchJob.Id,
                Jobs = jobResponses
            };
        }

        public async Task<JobStatusResponse> GetJobStatus(string jobId)
        {
            if (string.IsNullOrWhiteSpace(jobId))
                throw new ArgumentException("jobId не может быть пустым", nameof(jobId));

            var cacheKey = $"job-status:{jobId}";
            if (_memoryCache.TryGetValue(cacheKey, out JobStatusResponse? cached) && cached != null)
            {
                return cached;
            }

            // Сначала берем легкий статус без JSONB полей (чтобы polling не душил БД/сериализацию)
            var light = await _repository.GetJobStatusResponseAsync(jobId, includeDetails: false);
            if (light == null)
                throw new KeyNotFoundException($"Task with ID {jobId} not found");

            // Если задача завершена, можно вернуть детали (keyframes/audioAnalysis) — это реже и не так критично
            JobStatusResponse result = light;
            if (light.Status == ConversionStatus.Completed)
            {
                var detailed = await _repository.GetJobStatusResponseAsync(jobId, includeDetails: true);
                if (detailed != null)
                {
                    result = detailed;
                }
            }

            result.Progress = GetProgressFromStatus(result.Status);

            // Короткий кэш, чтобы частые запросы статуса не создавали шторм по БД
            _memoryCache.Set(cacheKey, result, new MemoryCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(1),
                Size = 1 // Указываем размер на случай, если SizeLimit будет включен обратно
            });

            return result;
        }

        public async Task<List<JobStatusResponse>> GetBatchStatus(string batchId)
        {
            if (string.IsNullOrWhiteSpace(batchId))
                throw new ArgumentException("batchId не может быть пустым", nameof(batchId));

            var cacheKey = $"batch-status:{batchId}";
            if (_memoryCache.TryGetValue(cacheKey, out List<JobStatusResponse>? cached) && cached != null)
            {
                return cached;
            }

            // Требование: возвращаем статусы вместе с JSONB (Keyframes/AudioAnalysis)
            // При этом оставляем короткий кэш, чтобы частый polling не создавал шторм по БД/сериализации.
            var jobs = await _repository.GetBatchStatusResponsesAsync(batchId, includeDetails: true);

            if (jobs.Count == 0)
            {
                _logger.LogInformation("Не найдено задач для пакета {BatchId}", batchId);
                return new List<JobStatusResponse>();
            }

            foreach (var j in jobs)
            {
                j.Progress = GetProgressFromStatus(j.Status);
            }

            _memoryCache.Set(cacheKey, jobs, new MemoryCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(1),
                Size = 1 // Указываем размер на случай, если SizeLimit будет включен обратно
            });

            return jobs;
        }

        public async Task<List<JobStatusResponse>> GetAllJobs(int skip = 0, int take = 20)
        {
            var jobs = await _repository.GetAllJobsAsync(skip, take);
            
            return jobs.Select(j => new JobStatusResponse
            {
                JobId = j.Id,
                Status = j.Status,
                VideoUrl = j.VideoUrl, 
                NewVideoUrl = j.NewVideoUrl,
                Mp3Url = j.Mp3Url,
                Keyframes = j.Keyframes,
                AudioAnalysis = j.AudioAnalysis, // Добавляем данные анализа аудио
                ErrorMessage = j.ErrorMessage,
                Progress = GetProgressFromStatus(j.Status)
            }).ToList();
        }

        public async Task<ConversionJob?> GetJobDetails(string jobId)
        {
            try
            {
                return await _repository.GetJobByIdAsync(jobId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Ошибка при получении деталей задачи {jobId}");
                return null;
            }
        }

        private double GetProgressFromStatus(ConversionStatus status)
        {
            return status switch
            {
                ConversionStatus.Pending => 0,
                ConversionStatus.Downloading => 20, 
                ConversionStatus.Converting => 40,
                ConversionStatus.AudioAnalyzing => 60,
                ConversionStatus.ExtractingKeyframes => 70,
                ConversionStatus.Uploading => 85,   
                ConversionStatus.Completed => 100,
                ConversionStatus.Failed => 0,    
                _ => 0
            };
        }

        // Статический метод UpdateJobStatusAsync остается без изменений в сигнатуре,
        // но логирование внутри улучшено
        public static async Task UpdateJobStatusAsync(
            IJobRepository repository, 
            string jobId, 
            ConversionStatus status, 
            string? mp3Url = null, 
            string? newVideoUrl = null,
            string? errorMessage = null)
        {
            using var scope = ServiceActivator.GetScope(); // Пытаемся получить scope
            ILogger? logger = scope?.ServiceProvider.GetService<ILogger<DbJobManager>>(); // Получаем логгер из scope;
            try
            {
                 logger?.LogDebug("Обновление статуса задачи {JobId} на {Status}. Mp3Url: {Mp3Url}, NewVideoUrl: {NewVideoUrl}, Ошибка: {Error}", 
                    jobId, status, mp3Url ?? "N/A", newVideoUrl ?? "N/A", errorMessage ?? "нет");

                await repository.UpdateJobStatusAsync(jobId, status, mp3Url, newVideoUrl, errorMessage);
            }
            catch (ObjectDisposedException ex)
            {
                logger.LogError(ex, "Объект контекста БД был уничтожен при обновлении статуса задачи {JobId}. Проверьте время жизни зависимостей.", jobId);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Ошибка при обновлении статуса задачи {JobId} на {Status}.", jobId, status);
            }
        }
        

        
    }
} 