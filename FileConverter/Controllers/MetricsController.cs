using Microsoft.AspNetCore.Mvc;
using FileConverter.Services;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace FileConverter.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class MetricsController : ControllerBase
    {
        private readonly ILogger<MetricsController> _logger;
        private readonly MetricsCollector _metricsCollector;
        private readonly ITempFileManager _tempFileManager;

        public MetricsController(
            ILogger<MetricsController> logger,
            MetricsCollector metricsCollector,
            ITempFileManager tempFileManager)
        {
            _logger = logger;
            _metricsCollector = metricsCollector;
            _tempFileManager = tempFileManager;
        }

        /// <summary>
        /// Возвращает сводку метрик производительности
        /// </summary>
        [HttpGet("summary")]
        [ProducesResponseType(typeof(MetricsSummary), 200)]
        public async Task<IActionResult> GetSummary()
        {
            try
            {
                // Собираем метрики производительности
                var metrics = _metricsCollector.GetMetrics();
                var requestStats = _metricsCollector.GetRequestStats();
                var conversionStats = _metricsCollector.GetConversionStats();
                
                // Собираем информацию о системе
                var process = Process.GetCurrentProcess();
                var systemInfo = new SystemInfo
                {
                    ProcessorCount = Environment.ProcessorCount,
                    OperatingSystem = RuntimeInformation.OSDescription,
                    MemoryUsageMb = process.WorkingSet64 / (1024 * 1024),
                    CpuTime = process.TotalProcessorTime,
                    StartTime = process.StartTime,
                    UpTime = DateTime.Now - process.StartTime,
                    ThreadCount = process.Threads.Count
                };
                
                // Получаем статистику по временным файлам
                var tempFileStats = await _tempFileManager.GetTempFileStatsAsync();
                
                // Формируем сводный отчет
                var summary = new MetricsSummary
                {
                    Timestamp = DateTime.UtcNow,
                    System = systemInfo,
                    Requests = new RequestStats
                    {
                        Total = requestStats.total,
                        Success = requestStats.success,
                        Failed = requestStats.failed,
                        SuccessRate = requestStats.total > 0 
                            ? (double)requestStats.success / requestStats.total * 100 
                            : 100
                    },
                    Conversions = new ConversionStats
                    {
                        Total = conversionStats.total,
                        Success = conversionStats.success,
                        Failed = conversionStats.failed,
                        Cached = conversionStats.cached,
                        SuccessRate = conversionStats.total > 0 
                            ? (double)conversionStats.success / conversionStats.total * 100 
                            : 100
                    },
                    TempFiles = new TempFileInfo
                    {
                        TotalFiles = tempFileStats.TotalFiles,
                        TotalSizeMb = tempFileStats.TotalSizeBytes / (1024 * 1024),
                        OldFiles = tempFileStats.OldFiles,
                        OldFilesSizeMb = tempFileStats.OldFilesSizeBytes / (1024 * 1024)
                    },
                    TopPerformanceMetrics = metrics
                        .OrderByDescending(m => m.Value.TotalCount)
                        .Take(10)
                        .Select(m => new PerformanceMetric
                        {
                            Name = m.Key,
                            TotalCount = m.Value.TotalCount,
                            SuccessCount = m.Value.SuccessCount,
                            FailureCount = m.Value.FailureCount,
                            AverageDurationMs = m.Value.TotalCount > 0 
                                ? (double)m.Value.TotalDurationMs / m.Value.TotalCount 
                                : 0,
                            MaxDurationMs = m.Value.MaxDurationMs,
                            SuccessRate = m.Value.SuccessRate
                        })
                        .ToList()
                };
                
                return Ok(summary);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting metrics");
                return StatusCode(500, "Error retrieving metrics");
            }
        }

        /// <summary>
        /// Возвращает состояние здоровья приложения
        /// </summary>
        [HttpGet("health")]
        [ProducesResponseType(typeof(HealthStatus), 200)]
        public async Task<IActionResult> GetHealth()
        {
            var requestStats = _metricsCollector.GetRequestStats();
            var conversionStats = _metricsCollector.GetConversionStats();
            var tempFileStats = await _tempFileManager.GetTempFileStatsAsync();
            
            // Проверяем метрики для определения здоровья
            bool isHealthy = true;
            var issues = new List<string>();
            
            // Проверка успешности запросов
            if (requestStats.total > 10 && (double)requestStats.success / requestStats.total < 0.9)
            {
                isHealthy = false;
                issues.Add($"Низкая успешность HTTP запросов: {(double)requestStats.success / requestStats.total:P2}");
            }
            
            // Проверка успешности конвертаций
            if (conversionStats.total > 10 && (double)conversionStats.success / conversionStats.total < 0.8)
            {
                isHealthy = false;
                issues.Add($"Низкая успешность конвертаций: {(double)conversionStats.success / conversionStats.total:P2}");
            }
            
            // Проверка использования памяти
            var process = Process.GetCurrentProcess();
            long memoryMb = process.WorkingSet64 / (1024 * 1024);
            if (memoryMb > 1024 * 2) // 2 ГБ
            {
                isHealthy = false;
                issues.Add($"Высокое использование памяти: {memoryMb} МБ");
            }
            
            // Проверка временных файлов
            if (tempFileStats.TotalSizeBytes > 5L * 1024 * 1024 * 1024) // 5 ГБ
            {
                isHealthy = false;
                issues.Add($"Слишком много временных файлов: {tempFileStats.TotalSizeBytes / (1024 * 1024 * 1024)} ГБ");
            }
            
            var health = new HealthStatus
            {
                Status = isHealthy ? "Healthy" : "Unhealthy",
                Timestamp = DateTime.UtcNow,
                Version = GetType().Assembly.GetName().Version?.ToString() ?? "Unknown",
                Issues = issues
            };
            
            return Ok(health);
        }

        /// <summary>
        /// Очищает старые временные файлы
        /// </summary>
        [HttpPost("cleanup-temp")]
        [ProducesResponseType(typeof(CleanupResult), 200)]
        public async Task<IActionResult> CleanupTempFiles([FromQuery] int hoursOld = 24)
        {
            try
            {
                if (hoursOld < 1) hoursOld = 1;
                if (hoursOld > 720) hoursOld = 720; // Максимум 30 дней
                
                var before = await _tempFileManager.GetTempFileStatsAsync();
                await _tempFileManager.CleanupOldTempFilesAsync(TimeSpan.FromHours(hoursOld));
                var after = await _tempFileManager.GetTempFileStatsAsync();
                
                var result = new CleanupResult
                {
                    Success = true,
                    HoursOld = hoursOld,
                    FilesRemoved = before.TotalFiles - after.TotalFiles,
                    SpaceFreedMb = (before.TotalSizeBytes - after.TotalSizeBytes) / (1024 * 1024)
                };
                
                return Ok(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error cleaning up temporary files");
                return StatusCode(500, "Error cleaning up temporary files");
            }
        }
    }

    // Классы для возврата данных метрик

    public class MetricsSummary
    {
        public DateTime Timestamp { get; set; }
        public SystemInfo System { get; set; } = new();
        public RequestStats Requests { get; set; } = new();
        public ConversionStats Conversions { get; set; } = new();
        public TempFileInfo TempFiles { get; set; } = new();
        public List<PerformanceMetric> TopPerformanceMetrics { get; set; } = new();
    }

    public class SystemInfo
    {
        public int ProcessorCount { get; set; }
        public string OperatingSystem { get; set; } = string.Empty;
        public long MemoryUsageMb { get; set; }
        public TimeSpan CpuTime { get; set; }
        public DateTime StartTime { get; set; }
        public TimeSpan UpTime { get; set; }
        public int ThreadCount { get; set; }
    }

    public class RequestStats
    {
        public long Total { get; set; }
        public long Success { get; set; }
        public long Failed { get; set; }
        public double SuccessRate { get; set; }
    }

    public class ConversionStats
    {
        public long Total { get; set; }
        public long Success { get; set; }
        public long Failed { get; set; }
        public long Cached { get; set; }
        public double SuccessRate { get; set; }
    }

    public class TempFileInfo
    {
        public int TotalFiles { get; set; }
        public long TotalSizeMb { get; set; }
        public int OldFiles { get; set; }
        public long OldFilesSizeMb { get; set; }
    }

    public class PerformanceMetric
    {
        public string Name { get; set; } = string.Empty;
        public long TotalCount { get; set; }
        public long SuccessCount { get; set; }
        public long FailureCount { get; set; }
        public double AverageDurationMs { get; set; }
        public long MaxDurationMs { get; set; }
        public double SuccessRate { get; set; }
    }

    public class HealthStatus
    {
        public string Status { get; set; } = string.Empty;
        public DateTime Timestamp { get; set; }
        public string Version { get; set; } = string.Empty;
        public List<string> Issues { get; set; } = new();
    }

    public class CleanupResult
    {
        public bool Success { get; set; }
        public int HoursOld { get; set; }
        public int FilesRemoved { get; set; }
        public long SpaceFreedMb { get; set; }
    }
} 