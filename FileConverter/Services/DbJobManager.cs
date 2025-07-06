using FileConverter.Data;
using FileConverter.Models;
using FileConverter.Services.Interfaces;
// using Hangfire; // Удаляем зависимость от Hangfire Client
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration; // Добавляем для Configuration
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
        private readonly ProcessingChannels _channels; // Добавляем каналы
        private readonly IConversionLogger _conversionLogger; // Добавляем логгер конверсий
        private readonly ILogger<DbJobManager> _logger;
        private readonly IConfiguration _configuration;
        private readonly IYoutubeDownloadService _youtubeDownloadService;
        
        public DbJobManager(
            IJobRepository repository,
            IMediaItemRepository mediaItemRepository,
            // IBackgroundJobClient backgroundJobClient, // Удаляем Hangfire Client
            ProcessingChannels channels, // Добавляем каналы
            IConversionLogger conversionLogger, // Добавляем логгер
            ILogger<DbJobManager> logger,
            IConfiguration configuration,
            IYoutubeDownloadService youtubeDownloadService)
        {
            _repository = repository;
            _mediaItemRepository = mediaItemRepository;
            // _backgroundJobClient = backgroundJobClient; // Удаляем Hangfire Client
            _channels = channels; // Сохраняем каналы
            _conversionLogger = conversionLogger; // Сохраняем логгер
            _logger = logger;
            _configuration = configuration;
            _youtubeDownloadService = youtubeDownloadService;
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
                
                // Запускаем задачу НЕ через Hangfire, а добавляем в канал напрямую
                try
                {
                    // Проверяем, является ли это YouTube видео
                    if (_youtubeDownloadService.IsYoutubeUrl(newJob.VideoUrl))
                    {
                        await _channels.YoutubeDownloadChannel.Writer.WriteAsync((newJob.Id, newJob.VideoUrl));
                        _logger.LogInformation("Задача {JobId} добавлена в очередь YouTube скачивания из EnqueueConversionJob.", newJob.Id);
                        await _conversionLogger.LogSystemInfoAsync($"Задача {newJob.Id} добавлена в очередь YouTube скачивания.");
                    }
                    else
                    {
                        await _channels.DownloadChannel.Writer.WriteAsync((newJob.Id, newJob.VideoUrl));
                        _logger.LogInformation("Задача {JobId} добавлена в очередь скачивания из EnqueueConversionJob.", newJob.Id);
                        await _conversionLogger.LogSystemInfoAsync($"Задача {newJob.Id} добавлена в очередь скачивания.");
                    }
                }
                catch (ChannelClosedException chEx)
                {
                    _logger.LogError(chEx, "Не удалось записать задачу {JobId} в очередь скачивания (канал закрыт) из EnqueueConversionJob.", newJob.Id);
                    await _conversionLogger.LogErrorAsync(newJob.Id, "Не удалось добавить задачу в очередь скачивания (канал закрыт).", chEx.StackTrace);
                    throw new InvalidOperationException($"Не удалось добавить задачу {newJob.Id} в очередь обработки (канал закрыт).", chEx);
                }
                 catch (Exception writeEx)
                {
                    _logger.LogError(writeEx, "Не удалось записать задачу {JobId} в очередь скачивания из EnqueueConversionJob.", newJob.Id);
                    await _conversionLogger.LogErrorAsync(newJob.Id, $"Не удалось добавить задачу в очередь скачивания: {writeEx.Message}", writeEx.StackTrace);
                    throw new InvalidOperationException($"Не удалось добавить задачу {newJob.Id} в очередь обработки.", writeEx);
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

                string? currentJobId = null;
                try 
                { 
                    var job = new ConversionJob 
                    { 
                        VideoUrl = videoUrl,
                        Status = ConversionStatus.Pending,
                        BatchId = batchJob.Id,
                        CreatedAt = DateTime.UtcNow,
                        LastAttemptAt = DateTime.UtcNow
                    };
                    currentJobId = job.Id;
                        
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
                    _logger.LogError(ex, "Ошибка при создании задачи {JobId} для URL {VideoUrl} в пакете {BatchId}", currentJobId ?? "N/A", videoUrl, batchJob.Id);
                     if(currentJobId != null) 
                     {
                         await _conversionLogger.LogErrorAsync(currentJobId, $"Ошибка при создании задачи в пакете {batchJob.Id}: {ex.Message}", ex.StackTrace, ConversionStatus.Failed);
                         // Статус Failed можно не ставить, т.к. задача даже не создалась
                     }
                     // Не прерываем весь пакет, но логируем ошибку
                }
            }

            _logger.LogInformation("Создано {CreatedCount} задач для пакета {BatchId}. Добавление в очередь...", createdJobs.Count, batchJob.Id);

            // Шаг 2: Ставим созданные задачи в очередь обработки
            int queuedCount = 0;
            foreach(var jobInfo in createdJobs)
            {
                 try 
                 { 
                     // Проверяем, является ли это YouTube видео
                     if (_youtubeDownloadService.IsYoutubeUrl(jobInfo.VideoUrl))
                     {
                         await _channels.YoutubeDownloadChannel.Writer.WriteAsync((jobInfo.JobId, jobInfo.VideoUrl));
                         _logger.LogInformation("Задача {JobId} из пакета {BatchId} добавлена в очередь YouTube скачивания.", jobInfo.JobId, batchJob.Id);
                         await _conversionLogger.LogSystemInfoAsync($"Задача {jobInfo.JobId} (пакет {batchJob.Id}) добавлена в очередь YouTube скачивания.");
                     }
                     else
                     {
                         await _channels.DownloadChannel.Writer.WriteAsync(jobInfo);
                         _logger.LogInformation("Задача {JobId} из пакета {BatchId} добавлена в очередь скачивания.", jobInfo.JobId, batchJob.Id);
                         await _conversionLogger.LogSystemInfoAsync($"Задача {jobInfo.JobId} (пакет {batchJob.Id}) добавлена в очередь скачивания.");
                     }
                     queuedCount++;
                 }
                 catch (ChannelClosedException chEx) 
                 {
                     _logger.LogError(chEx, "Не удалось записать задачу {JobId} из пакета {BatchId} в очередь скачивания (канал закрыт).", jobInfo.JobId, batchJob.Id);
                      await _conversionLogger.LogErrorAsync(jobInfo.JobId, "Не удалось добавить задачу в очередь скачивания (канал закрыт).", chEx.StackTrace);
                      await DbJobManager.UpdateJobStatusAsync(_repository, jobInfo.JobId, ConversionStatus.Failed, errorMessage: "Ошибка постановки в очередь (канал закрыт)");
                      // Прерываем добавление остальных задач, если канал закрыт?
                      // break;
                 }
                 catch (Exception ex)
                 {
                     _logger.LogError(ex, "Ошибка при добавлении задачи {JobId} из пакета {BatchId} в очередь скачивания.", jobInfo.JobId, batchJob.Id);
                      await _conversionLogger.LogErrorAsync(jobInfo.JobId, $"Ошибка при добавлении задачи в очередь скачивания: {ex.Message}", ex.StackTrace);
                      // Обновляем статус на Failed, так как задача создана, но не попала в очередь
                      await DbJobManager.UpdateJobStatusAsync(_repository, jobInfo.JobId, ConversionStatus.Failed, errorMessage: $"Ошибка постановки в очередь: {ex.Message}");
                 }
            }
             _logger.LogInformation("{QueuedCount} из {CreatedCount} задач пакета {BatchId} успешно добавлены в очередь.", queuedCount, createdJobs.Count, batchJob.Id);

            // Возвращаем результат с ID пакета
            return new BatchJobResult
            {
                BatchId = batchJob.Id,
                Jobs = jobResponses
            };
        }

        public async Task<JobStatusResponse> GetJobStatus(string jobId)
        {
            var job = await _repository.GetJobByIdAsync(jobId);
            
            if (job == null)
            {
                 throw new KeyNotFoundException($"Task with ID {jobId} not found");
            }
            
            return new JobStatusResponse
            {
                JobId = job.Id,
                Status = job.Status,
                VideoUrl = job.VideoUrl, 
                NewVideoUrl = job.NewVideoUrl, 
                Mp3Url = job.Mp3Url,
                KeyframeUrls = job.KeyframeUrls,
                ErrorMessage = job.ErrorMessage,
                Progress = GetProgressFromStatus(job.Status) 
            };
        }

        public async Task<List<JobStatusResponse>> GetBatchStatus(string batchId)
        {
            var jobs = await _repository.GetJobsByBatchIdAsync(batchId);
            
            if (!jobs.Any())
            {
                _logger.LogInformation("Не найдено задач для пакета {BatchId}", batchId);
                return new List<JobStatusResponse>(); 
            }
            
            return jobs.Select(job => new JobStatusResponse
            {
                JobId = job.Id,
                Status = job.Status,
                VideoUrl = job.VideoUrl,
                NewVideoUrl = job.NewVideoUrl,
                Mp3Url = job.Mp3Url,
                KeyframeUrls = job.KeyframeUrls,
                ErrorMessage = job.ErrorMessage,
                Progress = GetProgressFromStatus(job.Status)
            }).ToList();
        }

        public async Task<List<JobStatusResponse>> GetAllJobs(int skip = 0, int take = 20)
        {
            var jobs = await _repository.GetAllJobsAsync(skip, take);
            
            return jobs.Select(j => new JobStatusResponse
            {
                JobId = j.Id,
                Status = j.Status,
                 VideoUrl = j.VideoUrl, 
                Mp3Url = j.Mp3Url,
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
                ConversionStatus.Downloading => 25, 
                ConversionStatus.Converting => 50,  
                ConversionStatus.Uploading => 75,   
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
            List<string>? keyframeUrls = null,
            string? errorMessage = null)
        {
            using var scope = ServiceActivator.GetScope(); // Пытаемся получить scope
            ILogger? logger = scope?.ServiceProvider.GetService<ILogger<DbJobManager>>(); // Получаем логгер из scope;
            try
            {
                 logger?.LogDebug("Обновление статуса задачи {JobId} на {Status}. Mp3Url: {Mp3Url}, NewVideoUrl: {NewVideoUrl}, Ошибка: {Error}", 
                    jobId, status, mp3Url ?? "N/A", newVideoUrl ?? "N/A", errorMessage ?? "нет");

                await repository.UpdateJobStatusAsync(jobId, status, mp3Url, newVideoUrl, keyframeUrls, errorMessage);
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