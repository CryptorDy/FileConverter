using FileConverter.Models;
using Microsoft.EntityFrameworkCore;

namespace FileConverter.Data
{
    /// <summary>
    /// Репозиторий для работы с логами конвертации
    /// </summary>
    public class ConversionLogRepository : IConversionLogRepository
    {
        private readonly DbContextFactory _dbContextFactory;
        private readonly ILogger<ConversionLogRepository> _logger;
        
        /// <summary>
        /// Инициализирует новый экземпляр репозитория логов
        /// </summary>
        public ConversionLogRepository(
            DbContextFactory dbContextFactory,
            ILogger<ConversionLogRepository> logger)
        {
            _dbContextFactory = dbContextFactory;
            _logger = logger;
        }
        
        /// <inheritdoc/>
        public async Task<ConversionLogEvent> AddLogAsync(ConversionLogEvent logEvent)
        {
            return await _dbContextFactory.ExecuteWithDbContextAsync(async dbContext =>
            {
                await dbContext.ConversionLogs.AddAsync(logEvent);
                await dbContext.SaveChangesAsync();
                return logEvent;
            });
        }
        
        /// <inheritdoc/>
        public async Task<List<ConversionLogEvent>> GetLogsByJobIdAsync(string jobId)
        {
            return await _dbContextFactory.ExecuteWithDbContextAsync(async dbContext =>
            {
                return await dbContext.ConversionLogs
                    .Where(l => l.JobId == jobId)
                    .OrderByDescending(l => l.Timestamp)
                    .ToListAsync();
            });
        }
        
        /// <inheritdoc/>
        public async Task<List<ConversionLogEvent>> GetRecentLogsAsync(int count = 100)
        {
            return await _dbContextFactory.ExecuteWithDbContextAsync(async dbContext =>
            {
                return await dbContext.ConversionLogs
                    .OrderByDescending(l => l.Timestamp)
                    .Take(count)
                    .ToListAsync();
            });
        }
        
        /// <inheritdoc/>
        public async Task<List<ConversionLogEvent>> GetLogsByBatchIdAsync(string batchId)
        {
            return await _dbContextFactory.ExecuteWithDbContextAsync(async dbContext =>
            {
                return await dbContext.ConversionLogs
                    .Where(l => l.BatchId == batchId)
                    .OrderByDescending(l => l.Timestamp)
                    .ToListAsync();
            });
        }
        
        /// <inheritdoc/>
        public async Task<List<ConversionLogEvent>> GetLogsByEventTypeAsync(
            ConversionEventType eventType, 
            DateTime startTime, 
            DateTime endTime)
        {
            return await _dbContextFactory.ExecuteWithDbContextAsync(async dbContext =>
            {
                return await dbContext.ConversionLogs
                    .Where(l => l.EventType == eventType &&
                           l.Timestamp >= startTime &&
                           l.Timestamp <= endTime)
                    .OrderByDescending(l => l.Timestamp)
                    .ToListAsync();
            });
        }
        
        /// <inheritdoc/>
        public async Task<QueueStatistics> GetQueueStatisticsAsync(int hours = 24)
        {
            return await _dbContextFactory.ExecuteWithDbContextAsync(async dbContext =>
            {
                var startTime = DateTime.UtcNow.AddHours(-hours);
                var stats = new QueueStatistics { TimeRangeHours = hours };
                
                // Получаем все законченные задачи (успешно или с ошибкой) за указанный период
                var completedJobs = await dbContext.ConversionJobs
                    .Where(j => j.CompletedAt.HasValue && j.CompletedAt >= startTime)
                    .ToListAsync();
                
                // Получаем все задачи в работе
                var runningJobs = await dbContext.ConversionJobs
                    .Where(j => 
                        j.Status == ConversionStatus.Downloading || 
                        j.Status == ConversionStatus.Converting || 
                        j.Status == ConversionStatus.Uploading)
                    .ToListAsync();
                
                // Получаем задачи в очереди
                var pendingJobs = await dbContext.ConversionJobs
                    .Where(j => j.Status == ConversionStatus.Pending)
                    .ToListAsync();
                
                // Находим "застрявшие" задачи
                var staleThreshold = DateTime.UtcNow.AddMinutes(-30);
                var staleJobs = runningJobs
                    .Where(j => j.LastAttemptAt.HasValue && j.LastAttemptAt < staleThreshold)
                    .ToList();
                
                // Заполняем статистику
                stats.TotalJobs = completedJobs.Count + runningJobs.Count + pendingJobs.Count;
                stats.CompletedJobs = completedJobs.Count(j => j.Status == ConversionStatus.Completed);
                stats.FailedJobs = completedJobs.Count(j => j.Status == ConversionStatus.Failed);
                stats.StaleJobsCount = staleJobs.Count;
                
                // Текущие размеры очередей
                stats.CurrentDownloadQueueLength = runningJobs.Count(j => j.Status == ConversionStatus.Downloading) + 
                                                   pendingJobs.Count;
                stats.CurrentConversionQueueLength = runningJobs.Count(j => j.Status == ConversionStatus.Converting);
                stats.CurrentUploadQueueLength = runningJobs.Count(j => j.Status == ConversionStatus.Uploading);
                
                // Получаем события за указанный период для расчета времени
                var logsInPeriod = await dbContext.ConversionLogs
                    .Where(l => l.Timestamp >= startTime)
                    .ToListAsync();
                
                // Вычисляем средние времена
                CalculateAverageTimes(logsInPeriod, stats);
                
                return stats;
            });
        }
        
        private void CalculateAverageTimes(List<ConversionLogEvent> logs, QueueStatistics stats)
        {
            var jobDownloadTimes = new Dictionary<string, (DateTime? Start, DateTime? End, long? QueueTime)>();
            var jobConversionTimes = new Dictionary<string, (DateTime? Start, DateTime? End, long? QueueTime)>();
            var jobUploadTimes = new Dictionary<string, (DateTime? Start, DateTime? End, long? QueueTime)>();
            
            // Группируем логи по задачам и обрабатываем
            foreach (var log in logs)
            {
                // Обработка логов загрузки
                if (log.EventType == ConversionEventType.DownloadStarted)
                {
                    jobDownloadTimes[log.JobId] = (log.Timestamp, null, log.QueueTimeMs);
                }
                else if (log.EventType == ConversionEventType.DownloadCompleted && jobDownloadTimes.ContainsKey(log.JobId))
                {
                    var record = jobDownloadTimes[log.JobId];
                    jobDownloadTimes[log.JobId] = (record.Start, log.Timestamp, record.QueueTime);
                }
                
                // Обработка логов конвертации
                if (log.EventType == ConversionEventType.ConversionStarted)
                {
                    jobConversionTimes[log.JobId] = (log.Timestamp, null, log.QueueTimeMs);
                }
                else if (log.EventType == ConversionEventType.ConversionCompleted && jobConversionTimes.ContainsKey(log.JobId))
                {
                    var record = jobConversionTimes[log.JobId];
                    jobConversionTimes[log.JobId] = (record.Start, log.Timestamp, record.QueueTime);
                }
                
                // Обработка логов выгрузки
                if (log.EventType == ConversionEventType.UploadStarted)
                {
                    jobUploadTimes[log.JobId] = (log.Timestamp, null, log.QueueTimeMs);
                }
                else if (log.EventType == ConversionEventType.UploadCompleted && jobUploadTimes.ContainsKey(log.JobId))
                {
                    var record = jobUploadTimes[log.JobId];
                    jobUploadTimes[log.JobId] = (record.Start, log.Timestamp, record.QueueTime);
                }
            }
            
            // Вычисляем средние времена обработки
            CalculateAverageProcessingTime(jobDownloadTimes, out var avgDownloadTime, out var avgDownloadQueueTime);
            CalculateAverageProcessingTime(jobConversionTimes, out var avgConversionTime, out var avgConversionQueueTime);
            CalculateAverageProcessingTime(jobUploadTimes, out var avgUploadTime, out var avgUploadQueueTime);
            
            // Заполняем статистику
            stats.AverageDownloadTimeMs = avgDownloadTime;
            stats.AverageConversionTimeMs = avgConversionTime;
            stats.AverageUploadTimeMs = avgUploadTime;
            stats.AverageDownloadQueueTimeMs = avgDownloadQueueTime;
            stats.AverageConversionQueueTimeMs = avgConversionQueueTime;
            stats.AverageUploadQueueTimeMs = avgUploadQueueTime;
        }
        
        private void CalculateAverageProcessingTime(
            Dictionary<string, (DateTime? Start, DateTime? End, long? QueueTime)> jobTimes,
            out double avgProcessingTime,
            out double avgQueueTime)
        {
            var processingTimes = new List<double>();
            var queueTimes = new List<double>();
            
            foreach (var (jobId, times) in jobTimes)
            {
                if (times.Start.HasValue && times.End.HasValue)
                {
                    processingTimes.Add((times.End.Value - times.Start.Value).TotalMilliseconds);
                }
                
                if (times.QueueTime.HasValue)
                {
                    queueTimes.Add(times.QueueTime.Value);
                }
            }
            
            avgProcessingTime = processingTimes.Count > 0 ? processingTimes.Average() : 0;
            avgQueueTime = queueTimes.Count > 0 ? queueTimes.Average() : 0;
        }
        
        /// <inheritdoc/>
        public async Task<List<ConversionLogEvent>> GetErrorLogsAsync(
            DateTime startTime, 
            DateTime endTime)
        {
            return await _dbContextFactory.ExecuteWithDbContextAsync(async dbContext =>
            {
                return await dbContext.ConversionLogs
                    .Where(l => l.EventType == ConversionEventType.Error &&
                           l.Timestamp >= startTime &&
                           l.Timestamp <= endTime)
                    .OrderByDescending(l => l.Timestamp)
                    .ToListAsync();
            });
        }
        
        /// <inheritdoc/>
        public async Task<List<ConversionLogEvent>> GetStaleJobLogsAsync(int thresholdMinutes = 30)
        {
            return await _dbContextFactory.ExecuteWithDbContextAsync(async dbContext =>
            {
                // Находим ID задач, которые застряли
                var staleThreshold = DateTime.UtcNow.AddMinutes(-thresholdMinutes);
                
                var staleJobIds = await dbContext.ConversionJobs
                    .Where(j => (j.Status == ConversionStatus.Downloading ||
                               j.Status == ConversionStatus.Converting ||
                               j.Status == ConversionStatus.Uploading) &&
                               j.LastAttemptAt < staleThreshold)
                    .Select(j => j.Id)
                    .ToListAsync();
                
                if (!staleJobIds.Any())
                {
                    return new List<ConversionLogEvent>();
                }
                
                // Получаем последние логи для этих задач
                var jobLogs = new List<ConversionLogEvent>();
                
                foreach (var jobId in staleJobIds)
                {
                    var logs = await dbContext.ConversionLogs
                        .Where(l => l.JobId == jobId)
                        .OrderByDescending(l => l.Timestamp)
                        .Take(10) // Берем последние 10 записей для каждой задачи
                        .ToListAsync();
                    
                    jobLogs.AddRange(logs);
                }
                
                return jobLogs.OrderByDescending(l => l.Timestamp).ToList();
            });
        }
        
        /// <inheritdoc/>
        public async Task<int> PurgeOldLogsAsync(int thresholdDays = 30)
        {
            return await _dbContextFactory.ExecuteWithDbContextAsync(async dbContext =>
            {
                var cutoffDate = DateTime.UtcNow.AddDays(-thresholdDays);
                
                // Находим записи для удаления
                var logsToDelete = await dbContext.ConversionLogs
                    .Where(l => l.Timestamp < cutoffDate)
                    .ToListAsync();
                
                if (logsToDelete.Any())
                {
                    dbContext.ConversionLogs.RemoveRange(logsToDelete);
                    await dbContext.SaveChangesAsync();
                }
                
                return logsToDelete.Count;
            });
        }
    }
} 