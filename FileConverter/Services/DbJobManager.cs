using FileConverter.Data;
using FileConverter.Models;
using FileConverter.Services.Interfaces;
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
        
        public DbJobManager(
            IJobRepository repository,
            IBackgroundJobClient backgroundJobClient,
            ILogger<DbJobManager> logger,
            IConfiguration configuration)
        {
            _repository = repository;
            _backgroundJobClient = backgroundJobClient;
            _logger = logger;
            _configuration = configuration;
        }

        public async Task<ConversionJobResponse> EnqueueConversionJob(string videoUrl)
        {
            try
            {
                // Проверяем наличие URL
                if (string.IsNullOrEmpty(videoUrl))
                {
                    throw new ArgumentException("URL видео не может быть пустым");
                }
                
                // Создаем новую задачу конвертации
                var newJob = new ConversionJob
                {
                    VideoUrl = videoUrl,
                    Status = ConversionStatus.Pending
                };
                
                // Сохраняем в БД
                await _repository.CreateJobAsync(newJob);
                
                // Запускаем задачу в фоновом режиме
                _backgroundJobClient.Enqueue<IVideoConverter>(p => p.ProcessVideo(newJob.Id));
                
                // Формируем URL для проверки статуса
                string apiBaseUrl = _configuration["AppSettings:BaseUrl"] ?? "https://localhost:7134";
                
                // Возвращаем информацию о задаче
                return new ConversionJobResponse
                {
                    JobId = newJob.Id,
                    StatusUrl = $"{apiBaseUrl}/api/videoconverter/status/{newJob.Id}"
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error creating task for {videoUrl}");
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
                        // Создаем задачу
                        var job = new ConversionJob 
                        { 
                            VideoUrl = videoUrl,
                            Status = ConversionStatus.Pending,
                            BatchId = batchJob.Id
                        };
                        
                        await _repository.CreateJobAsync(job);
                        
                        // Запускаем обработку
                        _backgroundJobClient.Enqueue<IVideoConverter>(p => p.ProcessVideo(job.Id));
                        
                        var response = new ConversionJobResponse
                        {
                            JobId = job.Id,
                            StatusUrl = $"{apiBaseUrl}/api/videoconverter/status/{job.Id}"
                        };
                        
                        jobResponses.Add(response);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, $"Error processing {videoUrl}");
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
                _logger.LogError(ex, "Error creating batch task");
                throw;
            }
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
                throw new KeyNotFoundException($"Batch task with ID {batchId} not found");
            }
            
            return jobs.Select(job => new JobStatusResponse
            {
                JobId = job.Id,
                Status = job.Status,
                VideoUrl = job.VideoUrl,
                NewVideoUrl = job.NewVideoUrl,
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

        public async Task<ConversionJob?> GetJobDetails(string jobId)
        {
            try
            {
                return await _repository.GetJobByIdAsync(jobId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error getting job details for {jobId}");
                return null;
            }
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
            string jobId, 
            ConversionStatus status, 
            string? mp3Url = null, 
            string? newVideoUrl = null,
            string? errorMessage = null)
        {
            // Создаем логгер для использования в случае ошибок
            using var loggerFactory = LoggerFactory.Create(builder => builder.AddConsole());
            var logger = loggerFactory.CreateLogger("DbJobManager");
            
            try
            {
                await repository.UpdateJobStatusAsync(jobId, status, mp3Url, newVideoUrl, errorMessage);
            }
            catch (ObjectDisposedException ex)
            {
                logger.LogError(ex, "DbContext was disposed when updating job status for {JobId}. This might indicate context lifetime issues.", jobId);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error updating job status for {JobId}", jobId);
            }
        }
        
    }
} 