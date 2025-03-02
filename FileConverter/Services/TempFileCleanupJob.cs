using Hangfire;
using Microsoft.Extensions.Configuration;

namespace FileConverter.Services
{
    public class TempFileCleanupJob
    {
        private readonly ITempFileManager _tempFileManager;
        private readonly ILogger<TempFileCleanupJob> _logger;
        private readonly IConfiguration _configuration;

        public TempFileCleanupJob(
            ITempFileManager tempFileManager, 
            ILogger<TempFileCleanupJob> logger,
            IConfiguration configuration)
        {
            _tempFileManager = tempFileManager;
            _logger = logger;
            _configuration = configuration;
        }

        /// <summary>
        /// Запланировать периодическую очистку временных файлов
        /// </summary>
        public static void ScheduleJobs()
        {
            // Очистка файлов старше 24 часов каждый час
            RecurringJob.AddOrUpdate<TempFileCleanupJob>("cleanup-temp-daily", 
                job => job.CleanupOldTempFiles(TimeSpan.FromHours(24)), 
                Cron.Hourly);
                
            // Глубокая очистка и анализ каждую полночь
            RecurringJob.AddOrUpdate<TempFileCleanupJob>("cleanup-temp-deep",
                job => job.PerformDeepCleanup(),
                Cron.Daily);
        }

        /// <summary>
        /// Очистка временных файлов старше указанного возраста
        /// </summary>
        [AutomaticRetry(Attempts = 1)]
        public async Task CleanupOldTempFiles(TimeSpan age)
        {
            try
            {
                _logger.LogInformation($"Запуск очистки временных файлов старше {age.TotalHours:F1} часов");
                
                var statsBefore = await _tempFileManager.GetTempFileStatsAsync();
                await _tempFileManager.CleanupOldTempFilesAsync(age);
                var statsAfter = await _tempFileManager.GetTempFileStatsAsync();
                
                _logger.LogInformation($"Завершена очистка временных файлов. Было: {statsBefore.TotalFiles} файлов ({statsBefore.TotalSizeBytes / (1024.0 * 1024):F2} МБ), " +
                                      $"стало: {statsAfter.TotalFiles} файлов ({statsAfter.TotalSizeBytes / (1024.0 * 1024):F2} МБ)");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ошибка при очистке временных файлов");
                throw; // Позволяем Hangfire повторить задачу
            }
        }
        
        /// <summary>
        /// Выполняет глубокую очистку и анализ временных файлов
        /// </summary>
        [AutomaticRetry(Attempts = 2)]
        public async Task PerformDeepCleanup()
        {
            try
            {
                _logger.LogInformation("Запуск глубокой очистки временных файлов");
                
                // Сначала получаем статистику
                var stats = await _tempFileManager.GetTempFileStatsAsync();
                _logger.LogInformation($"Статистика временных файлов: {stats.TotalFiles} файлов ({stats.TotalSizeBytes / (1024.0 * 1024 * 1024):F2} ГБ), " +
                                      $"из них старше 24 часов: {stats.OldFiles} файлов ({stats.OldFilesSizeBytes / (1024.0 * 1024):F2} МБ)");
                
                // Сначала очищаем старые файлы (старше 24 часов)
                await _tempFileManager.CleanupOldTempFilesAsync(TimeSpan.FromHours(24));
                
                // Затем проверяем, нужна ли дополнительная очистка более новых файлов
                stats = await _tempFileManager.GetTempFileStatsAsync();
                
                // Если осталось больше 80% от максимального размера, очищаем файлы старше 12 часов
                long maxSize = long.TryParse(_configuration["FileConverter:MaxTempSizeBytes"], out long configMaxSize) 
                    ? configMaxSize 
                    : 10L * 1024 * 1024 * 1024; // 10 ГБ
                    
                if (stats.TotalSizeBytes > maxSize * 0.8)
                {
                    _logger.LogWarning($"Обнаружено большое использование места временными файлами: {stats.TotalSizeBytes / (1024.0 * 1024 * 1024):F2} ГБ");
                    await _tempFileManager.CleanupOldTempFilesAsync(TimeSpan.FromHours(12));
                    
                    // Если по-прежнему много места используется, очищаем еще более новые файлы
                    stats = await _tempFileManager.GetTempFileStatsAsync();
                    if (stats.TotalSizeBytes > maxSize * 0.7)
                    {
                        _logger.LogWarning("Выполняется агрессивная очистка временных файлов");
                        await _tempFileManager.CleanupOldTempFilesAsync(TimeSpan.FromHours(6));
                    }
                }
                
                // Финальная статистика
                stats = await _tempFileManager.GetTempFileStatsAsync();
                _logger.LogInformation($"Глубокая очистка завершена. Текущая статистика: {stats.TotalFiles} файлов ({stats.TotalSizeBytes / (1024.0 * 1024 * 1024):F2} ГБ)");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ошибка при глубокой очистке временных файлов");
                throw; // Позволяем Hangfire повторить задачу
            }
        }
    }
} 