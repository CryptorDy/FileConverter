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
    /// Фоновая служба для периодического выполнения задач восстановления
    /// зависших заданий и очистки старых логов.
    /// </summary>
    public class JobRecoveryHostedService : IHostedService, IDisposable
    {
        private readonly ILogger<JobRecoveryHostedService> _logger;
        private readonly IServiceProvider _serviceProvider;
        private readonly IConfiguration _configuration;
        private Timer? _recoveryTimer;
        private Timer? _cleanupTimer;
        private bool _recoveringJobs = false; // Флаг для предотвращения повторного входа
        private bool _cleaningLogs = false; // Флаг для предотвращения повторного входа

        public JobRecoveryHostedService(
            ILogger<JobRecoveryHostedService> logger,
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

            // Запускаем таймер для восстановления зависших заданий
            var recoveryIntervalMinutes = _configuration.GetValue<double>("Performance:RecoveryCheckIntervalMinutes", 10);
            var recoveryInterval = TimeSpan.FromMinutes(recoveryIntervalMinutes);
            _recoveryTimer = new Timer(DoRecoveryWork, null, TimeSpan.Zero, recoveryInterval); // Запустить сразу и потом по интервалу

            // Запускаем таймер для очистки старых логов
            var cleanupIntervalHours = _configuration.GetValue<int>("Performance:LogCleanupIntervalHours", 24); // По умолчанию раз в сутки
            var cleanupInterval = TimeSpan.FromHours(cleanupIntervalHours);
             // Запускаем чуть позже, чтобы не конфликтовать с первым запуском восстановления
            _cleanupTimer = new Timer(DoCleanupWork, null, TimeSpan.FromMinutes(1), cleanupInterval); 

            // Логирование настройки таймеров убрано для уменьшения количества логов

            return Task.CompletedTask;
        }

        private async void DoRecoveryWork(object? state)
        {
            if (_recoveringJobs)
            {
                _logger.LogWarning("Процесс восстановления заданий все еще выполняется. Пропуск текущего запуска.");
                return;
            }
            _recoveringJobs = true;
            // Логирование запуска проверки убрано для уменьшения количества логов

            try
            {
                using (var scope = _serviceProvider.CreateScope())
                {
                    var recoveryService = scope.ServiceProvider.GetRequiredService<IJobRecoveryService>();
                    await recoveryService.RecoverStaleJobsAsync();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ошибка во время периодического восстановления зависших заданий.");
            }
            finally
            {
                 _recoveringJobs = false;
            }
        }

        private async void DoCleanupWork(object? state)
        {
             if (_cleaningLogs)
            {
                _logger.LogWarning("Процесс очистки логов все еще выполняется. Пропуск текущего запуска.");
                return;
            }
            _cleaningLogs = true;
            // Логирование запуска очистки убрано для уменьшения количества логов

            try
            {
                using (var scope = _serviceProvider.CreateScope())
                {
                    var recoveryService = scope.ServiceProvider.GetRequiredService<IJobRecoveryService>();
                    // Получаем срок хранения логов из конфигурации, по умолчанию 30 дней
                    var logRetentionDays = _configuration.GetValue<int>("Performance:LogRetentionDays", 30); 
                    await recoveryService.CleanupOldLogsAsync(logRetentionDays);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ошибка во время периодической очистки старых логов.");
            }
             finally
            {
                 _cleaningLogs = false;
            }
        }

        public Task StopAsync(CancellationToken stoppingToken)
        {
            // Логирование остановки службы убрано для уменьшения количества логов

            _recoveryTimer?.Change(Timeout.Infinite, 0);
            _cleanupTimer?.Change(Timeout.Infinite, 0);

            return Task.CompletedTask;
        }

        public void Dispose()
        {
            _recoveryTimer?.Dispose();
            _cleanupTimer?.Dispose();
             GC.SuppressFinalize(this);
        }
    }
} 