using FileConverter.Data;
using FileConverter.Models;
using Hangfire;
using Microsoft.Extensions.Logging;

namespace FileConverter.Services
{
    public class DbJobManager : IJobManager
    {
        private readonly IJobRepository _repository;
        private readonly IBackgroundJobClient _backgroundJobClient;
        private readonly ILogger<DbJobManager> _logger;
        private readonly IConfiguration _configuration;
        private readonly CacheManager _cacheManager;
        
        public DbJobManager(
            IJobRepository repository,
            IBackgroundJobClient backgroundJobClient,
            ILogger<DbJobManager> logger,
            IConfiguration configuration,
            CacheManager cacheManager)
        {
            _repository = repository;
            _backgroundJobClient = backgroundJobClient;
            _logger = logger;
            _configuration = configuration;
            _cacheManager = cacheManager;
        }

        public async Task<ConversionJobResponse> EnqueueConversionJob(string videoUrl)
        {
            try
            {
                // Проверяем кэш
                if (_cacheManager.TryGetMp3Url(videoUrl, out string cachedMp3Url))
                {
                    _logger.LogInformation($"Найден кэшированный результат для {videoUrl}: {cachedMp3Url}");
                    
                    // Создаем задачу, но сразу помечаем как завершенную
                    var job = new ConversionJob 
                    { 
                        VideoUrl = videoUrl,
                        Status = ConversionStatus.Completed,
                        Mp3Url = cachedMp3Url,
                        CompletedAt = DateTime.UtcNow
                    };
                    
                    await _repository.CreateJobAsync(job);
                    
                    string baseUrl = _configuration["AppSettings:BaseUrl"] ?? "https://localhost:7134";
                    
                    return new ConversionJobResponse
                    {
                        JobId = job.Id,
                        StatusUrl = $"{baseUrl}/api/videoconverter/status/{job.Id}"
                    };
                }
                
                // Если не нашли в кэше, создаем новую задачу
                var newJob = new ConversionJob 
                { 
                    VideoUrl = videoUrl,
                    Status = ConversionStatus.Pending
                };
                
                await _repository.CreateJobAsync(newJob);
                
                // Запускаем задачу конвертации асинхронно через Hangfire
                _backgroundJobClient.Enqueue<IVideoProcessor>(p => p.ProcessVideo(newJob.Id));

                _logger.LogInformation($"Задача конвертации создана: {newJob.Id} для {videoUrl}");
                
                string apiBaseUrl = _configuration["AppSettings:BaseUrl"] ?? "https://localhost:7134";
                
                return new ConversionJobResponse
                {
                    JobId = newJob.Id,
                    StatusUrl = $"{apiBaseUrl}/api/videoconverter/status/{newJob.Id}"
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Ошибка создания задачи для {videoUrl}");
                throw;
            }
        }

        public async Task<BatchJobResult> EnqueueBatchJobs(List<string> videoUrls)
        {
            try
            {
                var batchJob = new BatchJob();
                await _repository.CreateBatchJobAsync(batchJob);
                
                var jobResponses = new List<ConversionJobResponse>();
                string apiBaseUrl = _configuration["AppSettings:BaseUrl"] ?? "https://localhost:7134";

                foreach (var videoUrl in videoUrls)
                {
                    try 
                    {
                        // Проверяем кэш
                        bool isCached = _cacheManager.TryGetMp3Url(videoUrl, out string cachedMp3Url);
                        
                        // Создаем задачу
                        var job = new ConversionJob 
                        { 
                            VideoUrl = videoUrl,
                            Status = isCached ? ConversionStatus.Completed : ConversionStatus.Pending,
                            BatchId = batchJob.Id
                        };
                        
                        if (isCached)
                        {
                            job.Mp3Url = cachedMp3Url;
                            job.CompletedAt = DateTime.UtcNow;
                            _logger.LogInformation($"Найден кэшированный результат для {videoUrl} в пакете {batchJob.Id}");
                        }
                        
                        await _repository.CreateJobAsync(job);
                        
                        // Если нет в кэше, запускаем обработку
                        if (!isCached)
                        {
                            _backgroundJobClient.Enqueue<IVideoProcessor>(p => p.ProcessVideo(job.Id));
                        }
                        
                        var response = new ConversionJobResponse
                        {
                            JobId = job.Id,
                            StatusUrl = $"{apiBaseUrl}/api/videoconverter/status/{job.Id}"
                        };
                        
                        jobResponses.Add(response);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, $"Ошибка при добавлении задачи для {videoUrl} в пакет {batchJob.Id}");
                        // Продолжаем с другими URL, не прерывая весь пакет
                    }
                }

                // Возвращаем результат с ID пакета
                return new BatchJobResult
                {
                    BatchId = batchJob.Id,
                    Jobs = jobResponses
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ошибка создания пакетной задачи");
                throw;
            }
        }

        public async Task<JobStatusResponse> GetJobStatus(string jobId)
        {
            var job = await _repository.GetJobByIdAsync(jobId);
            
            if (job == null)
            {
                throw new KeyNotFoundException($"Задача с ID {jobId} не найдена");
            }
            
            return new JobStatusResponse
            {
                JobId = job.Id,
                Status = job.Status,
                Mp3Url = job.Mp3Url,
                ErrorMessage = job.ErrorMessage,
                Progress = GetProgressFromStatus(job.Status)
            };
        }

        public async Task<List<JobStatusResponse>> GetBatchStatus(string batchId)
        {
            var jobs = await _repository.GetJobsByBatchIdAsync(batchId);
            
            if (!jobs.Any())
            {
                throw new KeyNotFoundException($"Пакет задач с ID {batchId} не найден");
            }
            
            return jobs.Select(job => new JobStatusResponse
            {
                JobId = job.Id,
                Status = job.Status,
                Mp3Url = job.Mp3Url,
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
                Mp3Url = j.Mp3Url,
                ErrorMessage = j.ErrorMessage,
                Progress = GetProgressFromStatus(j.Status)
            }).ToList();
        }

        // Возвращает прогресс конвертации на основе статуса
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
        
        // Статический метод для обновления статуса задачи через репозиторий
        public static async Task UpdateJobStatusAsync(
            IJobRepository repository, 
            CacheManager cacheManager,
            string jobId, 
            ConversionStatus status, 
            string? mp3Url = null, 
            string? errorMessage = null)
        {
            // Создаем логгер для использования в случае ошибок
            using var loggerFactory = LoggerFactory.Create(builder => builder.AddConsole());
            var logger = loggerFactory.CreateLogger("DbJobManager");
            
            try
            {
                var job = await repository.UpdateJobStatusAsync(jobId, status, mp3Url, errorMessage);
                
                // Кэшируем результат если успешно завершилась конвертация
                if (status == ConversionStatus.Completed && mp3Url != null)
                {
                    cacheManager.CacheMp3Url(job.VideoUrl, mp3Url);
                }
            }
            catch (ObjectDisposedException ex)
            {
                // Обрабатываем ошибку доступа к disposed контексту
                logger.LogError(ex, "Ошибка при обновлении статуса задачи {JobId}: Context был уничтожен. Статус: {Status}", 
                    jobId, status);
                
                // В этом случае мы не можем обновить статус задачи через базу данных,
                // но можем кэшировать результат, если это требуется
                if (status == ConversionStatus.Completed && mp3Url != null)
                {
                    try
                    {
                        // Просто сохраняем в кэш с примерным ключом
                        // Поскольку у нас нет возможности получить видео URL из уничтоженного контекста
                        // мы используем jobId как часть ключа
                        string estimatedKey = $"job_{jobId}";
                        logger.LogWarning("Сохраняем URL MP3 в кэш под примерным ключом: {Key}", estimatedKey);
                        cacheManager.CacheMp3Url(estimatedKey, mp3Url);
                    }
                    catch (Exception cacheEx)
                    {
                        logger.LogError(cacheEx, "Не удалось обновить кэш для задачи {JobId}", jobId);
                    }
                }
            }
            catch (Exception ex)
            {
                // Обработка других ошибок
                logger.LogError(ex, "Ошибка при обновлении статуса задачи {JobId}: {ErrorMessage}", 
                    jobId, ex.Message);
            }
        }
    }
} 