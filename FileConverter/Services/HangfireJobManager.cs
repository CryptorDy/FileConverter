using FileConverter.Models;
using Hangfire;
using System.Collections.Concurrent;

namespace FileConverter.Services
{
    public class HangfireJobManager : IJobManager
    {
        private readonly IBackgroundJobClient _backgroundJobClient;
        private readonly ILogger<HangfireJobManager> _logger;
        private readonly IConfiguration _configuration;
        private readonly CacheManager _cacheManager;
        
        // Храним задачи в памяти (в реальном приложении нужно использовать базу данных)
        private static readonly ConcurrentDictionary<string, ConversionJob> _jobs = new();
        private static readonly ConcurrentDictionary<string, List<string>> _batches = new();

        public HangfireJobManager(
            IBackgroundJobClient backgroundJobClient,
            ILogger<HangfireJobManager> logger,
            IConfiguration configuration,
            CacheManager cacheManager)
        {
            _backgroundJobClient = backgroundJobClient;
            _logger = logger;
            _configuration = configuration;
            _cacheManager = cacheManager;
        }

        public Task<ConversionJobResponse> EnqueueConversionJob(string videoUrl)
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
                    
                    _jobs.TryAdd(job.Id, job);
                    
                    string baseUrl = _configuration["AppSettings:BaseUrl"] ?? "https://localhost:7134";
                    
                    return Task.FromResult(new ConversionJobResponse
                    {
                        JobId = job.Id,
                        StatusUrl = $"{baseUrl}/api/videoconverter/status/{job.Id}"
                    });
                }
                
                // Если не нашли в кэше, создаем новую задачу
                var newJob = new ConversionJob 
                { 
                    VideoUrl = videoUrl,
                    Status = ConversionStatus.Pending
                };
                
                _jobs.TryAdd(newJob.Id, newJob);
                
                // Запускаем задачу конвертации асинхронно через Hangfire
                _backgroundJobClient.Enqueue<IVideoProcessor>(p => p.ProcessVideo(newJob.Id));

                _logger.LogInformation($"Задача конвертации создана: {newJob.Id} для {videoUrl}");
                
                string apiBaseUrl = _configuration["AppSettings:BaseUrl"] ?? "https://localhost:7134";
                
                return Task.FromResult(new ConversionJobResponse
                {
                    JobId = newJob.Id,
                    StatusUrl = $"{apiBaseUrl}/api/videoconverter/status/{newJob.Id}"
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Ошибка создания задачи для {videoUrl}");
                throw;
            }
        }

        public async Task<List<ConversionJobResponse>> EnqueueBatchJobs(List<string> videoUrls)
        {
            try
            {
                var batchId = Guid.NewGuid().ToString();
                var jobResponses = new List<ConversionJobResponse>();
                var jobIds = new List<string>();

                foreach (var videoUrl in videoUrls)
                {
                    var jobResponse = await EnqueueConversionJob(videoUrl);
                    jobResponses.Add(jobResponse);
                    jobIds.Add(jobResponse.JobId);
                }

                // Сохраняем связь партии с задачами
                _batches.TryAdd(batchId, jobIds);

                return jobResponses;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ошибка создания пакетной задачи");
                throw;
            }
        }

        public Task<JobStatusResponse> GetJobStatus(string jobId)
        {
            if (_jobs.TryGetValue(jobId, out var job))
            {
                return Task.FromResult(new JobStatusResponse
                {
                    JobId = job.Id,
                    Status = job.Status,
                    Mp3Url = job.Mp3Url,
                    ErrorMessage = job.ErrorMessage,
                    Progress = GetProgressFromStatus(job.Status)
                });
            }

            throw new KeyNotFoundException($"Задача с ID {jobId} не найдена");
        }

        public async Task<List<JobStatusResponse>> GetBatchStatus(string batchId)
        {
            if (_batches.TryGetValue(batchId, out var jobIds))
            {
                var statuses = new List<JobStatusResponse>();
                
                foreach (var jobId in jobIds)
                {
                    try
                    {
                        var status = await GetJobStatus(jobId);
                        statuses.Add(status);
                    }
                    catch
                    {
                        // Игнорируем ошибки для отдельных задач в пакете
                    }
                }
                
                return statuses;
            }

            throw new KeyNotFoundException($"Пакет задач с ID {batchId} не найден");
        }

        public Task<List<JobStatusResponse>> GetAllJobs(int skip = 0, int take = 20)
        {
            var jobs = _jobs.Values
                .OrderByDescending(j => j.CreatedAt)
                .Skip(skip)
                .Take(take)
                .Select(j => new JobStatusResponse
                {
                    JobId = j.Id,
                    Status = j.Status,
                    Mp3Url = j.Mp3Url,
                    ErrorMessage = j.ErrorMessage,
                    Progress = GetProgressFromStatus(j.Status)
                })
                .ToList();

            return Task.FromResult(jobs);
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

        // Обновляем метод UpdateJobStatus для кэширования результатов
        public static void UpdateJobStatus(string jobId, ConversionStatus status, string? mp3Url = null, string? errorMessage = null)
        {
            if (_jobs.TryGetValue(jobId, out var job))
            {
                job.Status = status;
                
                if (mp3Url != null)
                {
                    job.Mp3Url = mp3Url;
                    
                    // Кэшируем результат если успешно завершилась конвертация
                    if (status == ConversionStatus.Completed)
                    {
                        try
                        {
                            var cacheManager = GetCacheManager();
                            if (cacheManager != null)
                            {
                                cacheManager.CacheMp3Url(job.VideoUrl, mp3Url);
                            }
                        }
                        catch (Exception)
                        {
                            // Игнорируем ошибки кэширования
                        }
                    }
                }
                
                if (errorMessage != null)
                    job.ErrorMessage = errorMessage;
                
                if (status == ConversionStatus.Completed || status == ConversionStatus.Failed)
                    job.CompletedAt = DateTime.UtcNow;
            }
        }
        
        // Вспомогательный метод для получения CacheManager через DI контейнер
        private static CacheManager? GetCacheManager()
        {
            try
            {
                return ServiceActivator.GetScope()?.ServiceProvider.GetService<CacheManager>();
            }
            catch
            {
                return null;
            }
        }
    }
} 