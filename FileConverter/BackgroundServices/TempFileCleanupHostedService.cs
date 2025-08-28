using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Threading;
using System.Threading.Tasks;
using FileConverter.Services.Interfaces;

namespace FileConverter.BackgroundServices
{
    /// <summary>
    /// Фоновая служба для периодической очистки временных файлов.
    /// </summary>
    public class TempFileCleanupHostedService : IHostedService, IDisposable
    {
        private readonly ILogger<TempFileCleanupHostedService> _logger;
        private readonly IServiceProvider _serviceProvider;
        private readonly IConfiguration _configuration;
        private Timer? _cleanupTimer;
        private bool _cleaningFiles = false; // Флаг для предотвращения повторного входа

        // Ключи конфигурации
        private const string CleanupIntervalHoursKey = "Performance:TempFileCleanupIntervalHours";
        private const string TempFileMaxAgeHoursKey = "Performance:TempFileDefaultMaxAgeHours"; // Возраст для обычной очистки
        private const string MaxTempSizeKey = "Performance:MaxTempSizeBytes"; // Макс. размер папки временных файлов
        private const string AggressiveCleanupAgeHoursKey = "Performance:TempFileAggressiveMaxAgeHours"; // Возраст для агрессивной очистки
        private const string VeryAggressiveCleanupAgeHoursKey = "Performance:TempFileVeryAggressiveMaxAgeHours"; // Возраст для очень агрессивной очистки
        private const string HighUsageThresholdKey = "Performance:TempFileHighUsageThreshold"; // Порог для агрессивной очистки (0.0-1.0)
        private const string VeryHighUsageThresholdKey = "Performance:TempFileVeryHighUsageThreshold"; // Порог для очень агрессивной очистки (0.0-1.0)


        public TempFileCleanupHostedService(
            ILogger<TempFileCleanupHostedService> logger,
            IServiceProvider serviceProvider,
            IConfiguration configuration)
        {
            _logger = logger;
            _serviceProvider = serviceProvider;
            _configuration = configuration;
        }

        public Task StartAsync(CancellationToken stoppingToken)
        {
            // Логирование запуска службы убрано для уменьшения количества логов

            // Запускаем таймер для очистки
            var cleanupIntervalHours = _configuration.GetValue<int>(CleanupIntervalHoursKey, 24); // По умолчанию раз в сутки
            var cleanupInterval = TimeSpan.FromHours(cleanupIntervalHours);

            // Запускаем через некоторое время после старта, чтобы не мешать инициализации
            _cleanupTimer = new Timer(DoCleanupWork, null, TimeSpan.FromMinutes(5), cleanupInterval); 

            // Логирование настройки таймера убрано для уменьшения количества логов

            return Task.CompletedTask;
        }

        private async void DoCleanupWork(object? state)
        {
            if (_cleaningFiles)
            {
                _logger.LogWarning("Процесс очистки временных файлов все еще выполняется. Пропуск текущего запуска.");
                return;
            }
            _cleaningFiles = true;
            // Логирование запуска очистки убрано для уменьшения количества логов

            try
            {
                using (var scope = _serviceProvider.CreateScope())
                {
                    var tempFileManager = scope.ServiceProvider.GetRequiredService<ITempFileManager>();
                    
                    // Получаем параметры из конфигурации
                    var defaultAgeHours = _configuration.GetValue<int>(TempFileMaxAgeHoursKey, 24);
                    var aggressiveAgeHours = _configuration.GetValue<int>(AggressiveCleanupAgeHoursKey, 12);
                    var veryAggressiveAgeHours = _configuration.GetValue<int>(VeryAggressiveCleanupAgeHoursKey, 6);
                    long maxSize = _configuration.GetValue<long>(MaxTempSizeKey, 10L * 1024 * 1024 * 1024); // 10 GB default
                    double highUsageThreshold = _configuration.GetValue<double>(HighUsageThresholdKey, 0.8); // 80% default
                    double veryHighUsageThreshold = _configuration.GetValue<double>(VeryHighUsageThresholdKey, 0.7); // 70% default

                    // Выполняем логику, аналогичную PerformDeepCleanup
                    
                    // 1. Получаем начальную статистику
                    var stats = await tempFileManager.GetTempFileStatsAsync();
                    // Логирование статистики временных файлов убрано для уменьшения количества логов

                    // 2. Всегда выполняем стандартную очистку (старше defaultAgeHours)
                    // Логирование выполнения стандартной очистки убрано для уменьшения количества логов
                    await tempFileManager.CleanupOldTempFilesAsync(TimeSpan.FromHours(defaultAgeHours));
                    stats = await tempFileManager.GetTempFileStatsAsync(); // Обновляем статистику
                     // Логирование статистики после стандартной очистки убрано для уменьшения количества логов


                    // 3. Проверяем на высокое использование и выполняем агрессивную очистку при необходимости
                    if (maxSize > 0 && stats.TotalSizeBytes > maxSize * highUsageThreshold)
                    {
                        _logger.LogWarning($"Обнаружено высокое использование временной папки ({BytesToMegabytes(stats.TotalSizeBytes):F2} MB). Запуск агрессивной очистки (файлы старше {aggressiveAgeHours} часов)...");
                        await tempFileManager.CleanupOldTempFilesAsync(TimeSpan.FromHours(aggressiveAgeHours));
                        stats = await tempFileManager.GetTempFileStatsAsync(); // Обновляем статистику
                        // Логирование статистики после агрессивной очистки убрано для уменьшения количества логов

                        // 4. Проверяем на очень высокое использование и выполняем еще более агрессивную очистку
                         if (stats.TotalSizeBytes > maxSize * veryHighUsageThreshold)
                         {
                             _logger.LogWarning($"Использование временной папки все еще высокое ({BytesToMegabytes(stats.TotalSizeBytes):F2} MB). Запуск очень агрессивной очистки (файлы старше {veryAggressiveAgeHours} часов)...");
                             await tempFileManager.CleanupOldTempFilesAsync(TimeSpan.FromHours(veryAggressiveAgeHours));
                             stats = await tempFileManager.GetTempFileStatsAsync(); // Обновляем статистику
                             _logger.LogInformation($"Статистика после очень агрессивной очистки: {stats.TotalFiles} файлов ({BytesToMegabytes(stats.TotalSizeBytes):F2} MB)");
                         }
                    }

                    // Логирование завершения очистки убрано для уменьшения количества логов
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ошибка во время периодической очистки временных файлов.");
            }
             finally
            {
                 _cleaningFiles = false;
            }
        }

        public Task StopAsync(CancellationToken stoppingToken)
        {
            // Логирование остановки службы убрано для уменьшения количества логов
            _cleanupTimer?.Change(Timeout.Infinite, 0);
            return Task.CompletedTask;
        }

        public void Dispose()
        {
            _cleanupTimer?.Dispose();
             GC.SuppressFinalize(this);
        }

        private static double BytesToMegabytes(long bytes)
        {
            return bytes / (1024.0 * 1024.0);
        }
    }
} 