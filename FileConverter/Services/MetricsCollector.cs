using System.Collections.Concurrent;
using System.Diagnostics;

namespace FileConverter.Services
{
    /// <summary>
    /// Сервис для сбора метрик производительности
    /// </summary>
    public class MetricsCollector
    {
        private readonly ILogger<MetricsCollector> _logger;
        private readonly ConcurrentDictionary<string, MetricData> _metrics = new();
        private readonly ConcurrentDictionary<string, Stopwatch> _activeTimers = new();
        private readonly Timer _reportingTimer;
        private readonly DistributedCacheManager _cache;
        
        // Статистика запросов
        private long _totalRequests;
        private long _failedRequests;
        private long _successfulRequests;
        
        // Статистика задач конвертации
        private long _totalConversions;
        private long _failedConversions;
        private long _successfulConversions;
        private long _cachedResults;
        
        // Статистика загрузки системы
        private readonly ConcurrentQueue<(DateTime Time, double CpuUsage, long MemoryMb)> _systemStats = new();
        private readonly int _maxStatsHistory = 1000; // Хранить 1000 точек истории
        
        public MetricsCollector(
            ILogger<MetricsCollector> logger,
            IConfiguration configuration,
            DistributedCacheManager cache)
        {
            _logger = logger;
            _cache = cache;
            
            // Настройка таймера отчетности
            int reportingIntervalSec = configuration.GetValue<int>("Metrics:ReportingIntervalSeconds", 60);
            _reportingTimer = new Timer(ReportMetrics, null, 
                TimeSpan.FromSeconds(reportingIntervalSec), 
                TimeSpan.FromSeconds(reportingIntervalSec));
                
            _logger.LogInformation("Initialized metrics collector with reporting interval {Interval} seconds", reportingIntervalSec);
        }
        
        /// <summary>
        /// Начинает измерение времени для указанной операции
        /// </summary>
        public void StartTimer(string operation, string? context = null)
        {
            string key = GetKey(operation, context);
            var timer = new Stopwatch();
            timer.Start();
            _activeTimers[key] = timer;
        }
        
        /// <summary>
        /// Останавливает измерение времени для указанной операции и записывает результат
        /// </summary>
        public void StopTimer(string operation, string? context = null, bool isSuccess = true)
        {
            string key = GetKey(operation, context);
            
            if (_activeTimers.TryRemove(key, out Stopwatch? timer))
            {
                timer.Stop();
                RecordMetric(operation, timer.ElapsedMilliseconds, isSuccess, context);
            }
        }
        
        /// <summary>
        /// Записывает метрику без использования таймера
        /// </summary>
        public void RecordMetric(string operation, long durationMs, bool isSuccess = true, string? context = null)
        {
            var metric = _metrics.GetOrAdd(operation, _ => new MetricData(operation));
            
            if (isSuccess)
            {
                Interlocked.Increment(ref metric.SuccessCount);
            }
            else
            {
                Interlocked.Increment(ref metric.FailureCount);
            }
            
            Interlocked.Add(ref metric.TotalDurationMs, durationMs);
            
            // Обновляем максимальное и минимальное время
            long current;
            do
            {
                current = metric.MaxDurationMs;
                if (durationMs <= current) break;
            } while (Interlocked.CompareExchange(ref metric.MaxDurationMs, durationMs, current) != current);
            
            do
            {
                current = metric.MinDurationMs;
                if (current > 0 && durationMs >= current) break;
            } while (Interlocked.CompareExchange(ref metric.MinDurationMs, durationMs, current) != current);
            
            // Обновляем счетчики в зависимости от операции
            UpdateGlobalCounters(operation, isSuccess);
            
            // Логируем необычно длительные операции
            if (durationMs > 5000) // более 5 секунд
            {
                _logger.LogWarning(
                    "Long operation: {Operation}, Time: {Duration} ms, Context: {Context}",
                    operation, durationMs, context ?? "N/A");
            }
        }
        
        /// <summary>
        /// Записывает статистику использования системных ресурсов
        /// </summary>
        public void RecordSystemStats(double cpuUsagePercent, long memoryUsageMb)
        {
            // Добавляем новую точку статистики
            _systemStats.Enqueue((DateTime.UtcNow, cpuUsagePercent, memoryUsageMb));
            
            // Ограничиваем размер очереди
            while (_systemStats.Count > _maxStatsHistory && _systemStats.TryDequeue(out _)) { }
            
            // Логируем высокое использование ресурсов
            if (cpuUsagePercent > 80 || memoryUsageMb > 1024 * 8) // 80% CPU или 8 GB RAM
            {
                _logger.LogWarning(
                    "High resource usage: CPU {CpuUsage}%, Memory: {MemoryUsage} MB",
                    cpuUsagePercent, memoryUsageMb);
            }
        }
        
        /// <summary>
        /// Возвращает текущие метрики
        /// </summary>
        public Dictionary<string, MetricSummary> GetMetrics()
        {
            return _metrics.ToDictionary(
                kvp => kvp.Key,
                kvp => new MetricSummary(
                    kvp.Value.Name,
                    kvp.Value.SuccessCount,
                    kvp.Value.FailureCount,
                    kvp.Value.TotalDurationMs,
                    kvp.Value.MinDurationMs,
                    kvp.Value.MaxDurationMs
                ));
        }
        
        /// <summary>
        /// Возвращает статистику запросов
        /// </summary>
        public (long total, long success, long failed) GetRequestStats() => 
            (_totalRequests, _successfulRequests, _failedRequests);
            
        /// <summary>
        /// Возвращает статистику конвертаций
        /// </summary>
        public (long total, long success, long failed, long cached) GetConversionStats() => 
            (_totalConversions, _successfulConversions, _failedConversions, _cachedResults);
            
        /// <summary>
        /// Возвращает историю использования системных ресурсов
        /// </summary>
        public IEnumerable<(DateTime Time, double CpuUsage, long MemoryMb)> GetSystemStatsHistory() => 
            _systemStats.ToArray();
        
        // Вспомогательные методы
        private string GetKey(string operation, string? context) => 
            string.IsNullOrEmpty(context) ? operation : $"{operation}:{context}";
            
        private void UpdateGlobalCounters(string operation, bool isSuccess)
        {
            if (operation.StartsWith("http_"))
            {
                Interlocked.Increment(ref _totalRequests);
                if (isSuccess)
                    Interlocked.Increment(ref _successfulRequests);
                else
                    Interlocked.Increment(ref _failedRequests);
            }
            else if (operation.StartsWith("conversion_"))
            {
                Interlocked.Increment(ref _totalConversions);
                if (isSuccess)
                    Interlocked.Increment(ref _successfulConversions);
                else
                    Interlocked.Increment(ref _failedConversions);
            }
            else if (operation == "cache_hit")
            {
                Interlocked.Increment(ref _cachedResults);
            }
        }
        
        private async void ReportMetrics(object? state)
        {
            try
            {
                // Собираем данные о производительности
                var metrics = GetMetrics();
                var requestStats = GetRequestStats();
                var conversionStats = GetConversionStats();
                
                var summary = new
                {
                    Timestamp = DateTime.UtcNow,
                    UpTime = (DateTime.UtcNow - Process.GetCurrentProcess().StartTime.ToUniversalTime()).TotalHours,
                    Requests = new { 
                        Total = requestStats.total,
                        Success = requestStats.success,
                        Failed = requestStats.failed,
                        SuccessRate = requestStats.total > 0 
                            ? (double)requestStats.success / requestStats.total * 100 
                            : 100
                    },
                    Conversions = new {
                        Total = conversionStats.total,
                        Success = conversionStats.success,
                        Failed = conversionStats.failed,
                        Cached = conversionStats.cached,
                        SuccessRate = conversionStats.total > 0 
                            ? (double)conversionStats.success / conversionStats.total * 100 
                            : 100
                    },
                    TopMetrics = metrics
                        .OrderByDescending(m => m.Value.TotalCount)
                        .Take(10)
                        .Select(m => new {
                            Name = m.Key,
                            Count = m.Value.TotalCount,
                            AvgMs = m.Value.TotalCount > 0 
                                ? m.Value.TotalDurationMs / m.Value.TotalCount 
                                : 0,
                            MaxMs = m.Value.MaxDurationMs,
                            SuccessRate = m.Value.TotalCount > 0 
                                ? (double)m.Value.SuccessCount / m.Value.TotalCount * 100 
                                : 100
                        })
                        .ToList()
                };
                
                // Логируем сводку
                _logger.LogInformation(
                    "Performance metrics: Requests {TotalRequests} (Success: {SuccessRate:F1}%), " +
                    "Conversions {TotalConversions} (Success: {ConversionSuccessRate:F1}%, Cached: {CachedResults})",
                    summary.Requests.Total,
                    summary.Requests.SuccessRate,
                    summary.Conversions.Total,
                    summary.Conversions.SuccessRate,
                    summary.Conversions.Cached);
                
                // Кэшируем сводку для последующего использования в API
                await _cache.SetAsync("metrics:latest", summary, TimeSpan.FromMinutes(10));
                
                // Если есть проблемы с производительностью, логируем предупреждение
                if (summary.Requests.SuccessRate < 95 || summary.Conversions.SuccessRate < 90)
                {
                    _logger.LogWarning(
                        "Performance issues detected! " +
                        "Successful requests: {RequestSuccessRate:F1}%, Successful conversions: {ConversionSuccessRate:F1}%",
                        summary.Requests.SuccessRate,
                        summary.Conversions.SuccessRate);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error generating metrics report");
            }
        }
    }
    
    /// <summary>
    /// Класс для хранения данных метрики
    /// </summary>
    public class MetricData
    {
        public string Name { get; }
        public long SuccessCount;
        public long FailureCount;
        public long TotalDurationMs;
        public long MinDurationMs = long.MaxValue;
        public long MaxDurationMs;
        
        public long TotalCount => SuccessCount + FailureCount;
        
        public MetricData(string name)
        {
            Name = name;
        }
    }
    
    /// <summary>
    /// Класс для возврата сводки по метрике
    /// </summary>
    public record MetricSummary(
        string Name,
        long SuccessCount,
        long FailureCount,
        long TotalDurationMs,
        long MinDurationMs,
        long MaxDurationMs)
    {
        public long TotalCount => SuccessCount + FailureCount;
        
        public double AverageDurationMs => TotalCount > 0 
            ? (double)TotalDurationMs / TotalCount 
            : 0;
            
        public double SuccessRate => TotalCount > 0 
            ? (double)SuccessCount / TotalCount * 100 
            : 100;
    }
} 