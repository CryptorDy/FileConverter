using FileConverter.Data;
using FileConverter.Models;
using FileConverter.Services.Interfaces;
using Hangfire;

namespace FileConverter.Services
{
    /// <summary>
    /// Сервис для восстановления "застрявших" заданий
    /// </summary>
    public class JobRecoveryService : IJobRecoveryService
    {
        private readonly IJobRepository _jobRepository;
        private readonly IConversionLogRepository _logRepository;
        private readonly IBackgroundJobClient _backgroundJobClient;
        private readonly ILogger<JobRecoveryService> _logger;
        private readonly IConversionLogger _conversionLogger;
        private readonly IConfiguration _configuration;
        
        // Настройки восстановления заданий
        private readonly int _staleJobThresholdMinutes;
        private readonly int _maxAttempts;
        
        /// <summary>
        /// Инициализирует новый экземпляр сервиса восстановления заданий
        /// </summary>
        public JobRecoveryService(
            IJobRepository jobRepository,
            IConversionLogRepository logRepository,
            IBackgroundJobClient backgroundJobClient,
            ILogger<JobRecoveryService> logger,
            IConversionLogger conversionLogger,
            IConfiguration configuration)
        {
            _jobRepository = jobRepository;
            _logRepository = logRepository;
            _backgroundJobClient = backgroundJobClient;
            _logger = logger;
            _conversionLogger = conversionLogger;
            _configuration = configuration;
            
            // Загружаем настройки
            _staleJobThresholdMinutes = _configuration.GetValue<int>("Performance:StaleJobThresholdMinutes", 30);
            _maxAttempts = _configuration.GetValue<int>("Performance:JobRetryLimit", 3);
        }
        
        /// <inheritdoc/>
        public void ScheduleRecoveryJobs()
        {
            // Расписание для восстановления зависших заданий - каждые 10 минут
            RecurringJob.AddOrUpdate<IJobRecoveryService>(
                "recover-stale-jobs",
                service => service.RecoverStaleJobsAsync(),
                "*/10 * * * *");
            
            // Расписание для очистки старых логов - раз в день в 3:00
            RecurringJob.AddOrUpdate<IJobRecoveryService>(
                "cleanup-old-logs",
                service => service.CleanupOldLogsAsync(30),
                "0 3 * * *");
            
            _logger.LogInformation("Scheduled job recovery and log cleanup tasks");
        }
        
        /// <inheritdoc/>
        public async Task<int> RecoverStaleJobsAsync()
        {
            try
            {
                _logger.LogInformation("Starting recovery of stale jobs...");
                
                // Получаем все "застрявшие" задания
                var staleJobs = await _jobRepository.GetStaleJobsAsync(TimeSpan.FromMinutes(_staleJobThresholdMinutes));
                
                if (!staleJobs.Any())
                {
                    _logger.LogInformation("No stale jobs found");
                    return 0;
                }
                
                _logger.LogWarning("Found {Count} stale jobs", staleJobs.Count);
                
                int recoveredCount = 0;
                
                // Обрабатываем каждое "застрявшее" задание
                foreach (var job in staleJobs)
                {
                    try
                    {
                        bool recovered = await RecoverJobAsync(job);
                        if (recovered)
                        {
                            recoveredCount++;
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error recovering job {JobId}: {Message}", job.Id, ex.Message);
                    }
                }
                
                await _conversionLogger.LogSystemInfoAsync(
                    $"Восстановлено {recoveredCount} из {staleJobs.Count} застрявших заданий");
                
                return recoveredCount;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in RecoverStaleJobsAsync: {Message}", ex.Message);
                return 0;
            }
        }
        
        /// <summary>
        /// Восстанавливает конкретное "застрявшее" задание
        /// </summary>
        /// <param name="job">Задание для восстановления</param>
        /// <returns>true, если задание успешно восстановлено</returns>
        private async Task<bool> RecoverJobAsync(ConversionJob job)
        {
            // Если превышено количество попыток - отмечаем как Failed
            if (job.ProcessingAttempts >= _maxAttempts)
            {
                await _conversionLogger.LogJobCancelledAsync(job.Id, 
                    $"Превышено максимальное количество попыток ({_maxAttempts})");
                
                job.Status = ConversionStatus.Failed;
                job.ErrorMessage = $"Превышено максимальное количество попыток ({_maxAttempts})";
                job.CompletedAt = DateTime.UtcNow;
                
                await _jobRepository.UpdateJobAsync(job);
                return true;
            }
            
            // В зависимости от статуса восстанавливаем задание
            var previousStatus = job.Status;
            var reason = $"Задание не обновлялось более {_staleJobThresholdMinutes} минут в статусе {job.Status}";
            
            // Сбрасываем статус до Pending для повторного запуска
            job.Status = ConversionStatus.Pending;
            job.ProcessingAttempts++; // Увеличиваем счетчик попыток
            
            await _jobRepository.UpdateJobAsync(job);
            
            // Логируем восстановление задания
            await _conversionLogger.LogJobRecoveredAsync(
                job.Id, previousStatus, job.Status, reason);
            
            // Повторно запускаем обработку через Hangfire
            _backgroundJobClient.Enqueue<IVideoConverter>(p => p.ProcessVideo(job.Id));
            
            _logger.LogWarning("Recovered job {JobId} from {PreviousStatus} to {NewStatus}, attempt {Attempt}/{MaxAttempts}", 
                job.Id, previousStatus, job.Status, job.ProcessingAttempts, _maxAttempts);
                
            return true;
        }
        
        /// <inheritdoc/>
        public async Task<int> CleanupOldLogsAsync(int days = 30)
        {
            try
            {
                _logger.LogInformation("Starting cleanup of logs older than {Days} days", days);
                
                int deletedCount = await _logRepository.PurgeOldLogsAsync(days);
                
                if (deletedCount > 0)
                {
                    _logger.LogInformation("Deleted {Count} old log entries", deletedCount);
                    await _conversionLogger.LogSystemInfoAsync(
                        $"Удалено {deletedCount} устаревших записей логов старше {days} дней");
                }
                else
                {
                    _logger.LogInformation("No old log entries to delete");
                }
                
                return deletedCount;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in CleanupOldLogsAsync: {Message}", ex.Message);
                return 0;
            }
        }
        
        /// <inheritdoc/>
        public async Task<QueueStatisticsResult> GetQueueStatisticsAsync(int hours = 24)
        {
            try
            {
                var statistics = await _logRepository.GetQueueStatisticsAsync(hours);
                
                return new QueueStatisticsResult
                {
                    Success = true,
                    Statistics = statistics
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting queue statistics: {Message}", ex.Message);
                
                return new QueueStatisticsResult
                {
                    Success = false,
                    ErrorMessage = ex.Message
                };
            }
        }
    }
} 