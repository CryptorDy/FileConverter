using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FileConverter.Services
{
    /// <summary>
    /// Сервис для мониторинга и ограничения загрузки CPU до заданного порога
    /// </summary>
    public class CpuThrottleService : BackgroundService
    {
        private readonly ILogger<CpuThrottleService> _logger;
        private readonly double _maxCpuUsagePercent;
        private readonly int _monitoringIntervalMs;
        private readonly int _checkIntervalMs;
        
        private double _currentCpuUsage = 0; // Не используем volatile для double, используем lock для синхронизации
        private readonly Process _currentProcess;
        private DateTime _lastCpuCheck = DateTime.UtcNow;
        private TimeSpan _lastCpuTime;
        private readonly object _updateLock = new object(); // Для синхронизации обновлений
        
        // Для экспоненциального сглаживания (EMA - Exponential Moving Average)
        private double _smoothedCpuUsage = 0;
        private readonly double _smoothingFactor = 0.3; // Коэффициент сглаживания (0-1, чем меньше - тем больше сглаживание)
        
        public CpuThrottleService(
            ILogger<CpuThrottleService> logger,
            IConfiguration configuration)
        {
            _logger = logger;
            _currentProcess = Process.GetCurrentProcess();
            _lastCpuTime = _currentProcess.TotalProcessorTime;
            
            // Получаем настройки из конфигурации
            _maxCpuUsagePercent = configuration.GetValue<double>("Performance:MaxCpuUsagePercent", 90.0);
            _monitoringIntervalMs = configuration.GetValue<int>("Performance:CpuMonitoringIntervalMs", 2000); // Проверка каждые 2 секунды
            _checkIntervalMs = configuration.GetValue<int>("Performance:CpuCheckIntervalMs", 500); // Проверка CPU каждые 500мс
            
            // Минимальный интервал для точного измерения (меньше 500мс - неточно)
            if (_monitoringIntervalMs < 500)
            {
                _logger.LogWarning("CpuMonitoringIntervalMs слишком мал ({Interval}мс), рекомендуется минимум 500мс для точных измерений", _monitoringIntervalMs);
            }
            
            _logger.LogInformation(
                "CpuThrottleService инициализирован. Максимальная загрузка CPU: {MaxCpu}%, интервал мониторинга: {Interval}мс",
                _maxCpuUsagePercent, _monitoringIntervalMs);
        }
        
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("CpuThrottleService запущен");
            
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    UpdateCpuUsage();
                    await Task.Delay(_monitoringIntervalMs, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Ошибка при мониторинге CPU");
                    await Task.Delay(_monitoringIntervalMs, stoppingToken);
                }
            }
            
            _logger.LogInformation("CpuThrottleService остановлен");
        }
        
        /// <summary>
        /// Обновляет текущую загрузку CPU
        /// </summary>
        private void UpdateCpuUsage()
        {
            lock (_updateLock)
            {
                try
                {
                    var now = DateTime.UtcNow;
                    var currentCpuTime = _currentProcess.TotalProcessorTime;
                    var elapsed = (now - _lastCpuCheck).TotalMilliseconds;
                    
                    if (elapsed > 0)
                    {
                        var cpuTimeElapsed = (currentCpuTime - _lastCpuTime).TotalMilliseconds;
                        var rawCpuUsage = (cpuTimeElapsed / elapsed) * 100.0 / Environment.ProcessorCount;
                        
                        // Ограничиваем значение от 0 до 100
                        var cpuUsage = Math.Max(0, Math.Min(100, rawCpuUsage));
                        
                        // Применяем экспоненциальное сглаживание для уменьшения колебаний
                        if (_smoothedCpuUsage == 0)
                        {
                            // Первое значение - используем как есть
                            _smoothedCpuUsage = cpuUsage;
                        }
                        else
                        {
                            // EMA: новое_значение = старое_значение * (1 - alpha) + новое_измерение * alpha
                            _smoothedCpuUsage = _smoothedCpuUsage * (1 - _smoothingFactor) + cpuUsage * _smoothingFactor;
                        }
                        
                        _currentCpuUsage = _smoothedCpuUsage;
                    }
                    
                    _lastCpuCheck = now;
                    _lastCpuTime = currentCpuTime;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Не удалось обновить загрузку CPU");
                }
            }
        }
        
        /// <summary>
        /// Возвращает текущую загрузку CPU в процентах
        /// </summary>
        public double GetCurrentCpuUsage()
        {
            // Обновляем загрузку перед возвратом, если прошло достаточно времени
            var timeSinceLastCheck = (DateTime.UtcNow - _lastCpuCheck).TotalMilliseconds;
            if (timeSinceLastCheck >= _checkIntervalMs)
            {
                UpdateCpuUsage();
            }
            
            // Безопасное чтение с использованием lock
            lock (_updateLock)
            {
                return _currentCpuUsage;
            }
        }
        
        /// <summary>
        /// Вычисляет необходимую задержку для снижения загрузки CPU до целевого уровня
        /// </summary>
        /// <param name="baseDelayMs">Базовая задержка в миллисекундах</param>
        /// <returns>Задержка в миллисекундах</returns>
        public int CalculateThrottleDelay(int baseDelayMs = 10)
        {
            var cpuUsage = GetCurrentCpuUsage();
            
            if (cpuUsage <= _maxCpuUsagePercent)
            {
                return 0; // Задержка не нужна
            }
            
            // Вычисляем превышение над лимитом
            var excess = cpuUsage - _maxCpuUsagePercent;
            
            // Чем больше превышение, тем больше задержка
            // Формула: задержка = базовая_задержка * (превышение / 10)
            // Например, при 95% и лимите 90%: excess = 5, задержка = 10 * (5/10) = 5мс
            // При 100%: excess = 10, задержка = 10 * (10/10) = 10мс
            var delay = (int)(baseDelayMs * (excess / 10.0));
            
            // Ограничиваем максимальную задержку до 100мс
            return Math.Min(100, Math.Max(0, delay));
        }
        
        /// <summary>
        /// Ожидает, если необходимо, для снижения загрузки CPU
        /// </summary>
        /// <param name="cancellationToken">Токен отмены</param>
        public async Task WaitIfNeededAsync(CancellationToken cancellationToken = default)
        {
            var delay = CalculateThrottleDelay();
            if (delay > 0)
            {
                await Task.Delay(delay, cancellationToken);
            }
        }
        
        /// <summary>
        /// Проверяет, нужно ли применить throttling
        /// </summary>
        public bool ShouldThrottle()
        {
            return GetCurrentCpuUsage() > _maxCpuUsagePercent;
        }
    }
}

