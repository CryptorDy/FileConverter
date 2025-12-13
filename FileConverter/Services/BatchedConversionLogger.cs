using FileConverter.Data;
using FileConverter.Models;
using FileConverter.Services.Interfaces;
using System.Collections.Concurrent;
using System.Diagnostics;

namespace FileConverter.Services
{
    /// <summary>
    /// Сервис логирования с батчингом - накапливает логи в памяти и пишет пачками для снижения нагрузки на БД
    /// </summary>
    public class BatchedConversionLogger : IConversionLogger, IDisposable
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly ILogger<BatchedConversionLogger> _logger;
        
        // Отслеживание времени начала задач
        private readonly ConcurrentDictionary<string, Stopwatch> _jobTimers = new();
        private readonly ConcurrentDictionary<string, (DateTime Start, ConversionStatus Status)> _stageTimers = new();
        
        // Батчинг: очередь логов и фоновый воркер
        private readonly ConcurrentQueue<ConversionLogEvent> _logQueue = new();
        private readonly Timer _flushTimer;
        private readonly SemaphoreSlim _flushSemaphore = new(1, 1);
        private bool _disposed = false;
        private const int BATCH_SIZE = 50; // Максимум логов в одной транзакции
        private const int FLUSH_INTERVAL_MS = 3000; // Пишем каждые 3 секунды

        public BatchedConversionLogger(
            IServiceProvider serviceProvider,
            ILogger<BatchedConversionLogger> logger)
        {
            _serviceProvider = serviceProvider;
            _logger = logger;
            
            // Запускаем таймер для периодического сброса логов
            _flushTimer = new Timer(async _ => await FlushLogsAsync(), null, TimeSpan.FromMilliseconds(FLUSH_INTERVAL_MS), TimeSpan.FromMilliseconds(FLUSH_INTERVAL_MS));
        }

        /// <summary>
        /// Добавляет лог-событие в очередь (не пишет в БД сразу)
        /// </summary>
        private void EnqueueLogEvent(ConversionLogEvent logEvent)
        {
            logEvent.Timestamp = DateTime.UtcNow;
            _logQueue.Enqueue(logEvent);
            
            // Если очередь переполнена, принудительно сбрасываем
            if (_logQueue.Count >= BATCH_SIZE * 2)
            {
                _ = Task.Run(() => FlushLogsAsync()); // Fire-and-forget
            }
        }

        /// <summary>
        /// Сбрасывает накопленные логи в БД пачкой
        /// </summary>
        private async Task FlushLogsAsync()
        {
            if (_logQueue.IsEmpty)
                return;

            // Только один поток может сбрасывать логи
            if (!await _flushSemaphore.WaitAsync(0))
                return;

            try
            {
                var batch = new List<ConversionLogEvent>();
                
                // Собираем пачку (до BATCH_SIZE элементов)
                while (batch.Count < BATCH_SIZE && _logQueue.TryDequeue(out var logEvent))
                {
                    batch.Add(logEvent);
                }

                if (batch.Count == 0)
                    return;

                // Пишем пачкой в БД через новый scope
                using var scope = _serviceProvider.CreateScope();
                var logRepository = scope.ServiceProvider.GetRequiredService<IConversionLogRepository>();
                await logRepository.CreateLogBatchAsync(batch);
                
                _logger.LogTrace("Записано {Count} логов конвертации в БД (батч)", batch.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ошибка при сбросе пачки логов в БД");
            }
            finally
            {
                _flushSemaphore.Release();
            }
        }

        public async Task LogJobCreatedAsync(string jobId, string videoUrl, string? batchId = null)
        {
            var stopwatch = new Stopwatch();
            stopwatch.Start();
            _jobTimers[jobId] = stopwatch;
            
            EnqueueLogEvent(new ConversionLogEvent
            {
                JobId = jobId,
                BatchId = batchId,
                EventType = ConversionEventType.JobCreated,
                JobStatus = ConversionStatus.Pending,
                Message = $"Задача создана для видео {videoUrl}",
                VideoUrl = videoUrl
            });
            
            await Task.CompletedTask;
        }

        public async Task LogJobQueuedAsync(string jobId, string videoUrl, string? details = null)
        {
            EnqueueLogEvent(new ConversionLogEvent
            {
                JobId = jobId,
                EventType = ConversionEventType.JobQueued,
                JobStatus = ConversionStatus.Pending,
                Message = "Задача поставлена в очередь конвертации",
                VideoUrl = videoUrl,
                Details = details
            });
            
            await Task.CompletedTask;
        }

        public async Task LogStatusChangedAsync(string jobId, ConversionStatus status, string? details = null, int attemptNumber = 1)
        {
            EnqueueLogEvent(new ConversionLogEvent
            {
                JobId = jobId,
                EventType = ConversionEventType.StatusChanged,
                JobStatus = status,
                Message = $"Статус изменен на {status}",
                Details = details,
                Step = attemptNumber
            });
            
            await Task.CompletedTask;
        }

        public async Task LogDownloadStartedAsync(string jobId, string videoUrl, long queueTimeMs)
        {
            _stageTimers[jobId] = (DateTime.UtcNow, ConversionStatus.Downloading);
            
            EnqueueLogEvent(new ConversionLogEvent
            {
                JobId = jobId,
                EventType = ConversionEventType.DownloadStarted,
                JobStatus = ConversionStatus.Downloading,
                Message = "Начато скачивание видео",
                VideoUrl = videoUrl,
                QueueTimeMs = queueTimeMs
            });
            
            await Task.CompletedTask;
        }

        public async Task LogDownloadProgressAsync(string jobId, long bytesReceived, long? totalBytes = null, double? rateBytes = null)
        {
            // Progress логи не пишем в БД вообще (слишком частые) - только метрики
            await Task.CompletedTask;
        }

        public async Task LogDownloadCompletedAsync(string jobId, long fileSizeBytes, string? path = null)
        {
            EnqueueLogEvent(new ConversionLogEvent
            {
                JobId = jobId,
                EventType = ConversionEventType.DownloadCompleted,
                JobStatus = ConversionStatus.Downloading,
                Message = $"Скачивание завершено, размер: {FormatBytes(fileSizeBytes)}",
                Details = path != null ? $"Сохранено в: {path}" : null,
                FileSizeBytes = fileSizeBytes
            });
            
            await Task.CompletedTask;
        }

        public async Task LogConversionStartedAsync(string jobId, long queueTimeMs, string? details = null)
        {
            _stageTimers[jobId] = (DateTime.UtcNow, ConversionStatus.Converting);
            
            EnqueueLogEvent(new ConversionLogEvent
            {
                JobId = jobId,
                EventType = ConversionEventType.ConversionStarted,
                JobStatus = ConversionStatus.Converting,
                Message = "Начата конвертация в MP3",
                Details = details,
                QueueTimeMs = queueTimeMs
            });
            
            await Task.CompletedTask;
        }

        public async Task LogConversionProgressAsync(string jobId, double percent, double? timeRemainingSeconds = null)
        {
            // Progress логи не пишем в БД (слишком частые)
            await Task.CompletedTask;
        }

        public async Task LogConversionCompletedAsync(string jobId, long fileSizeBytes, double durationSeconds, string? path = null)
        {
            EnqueueLogEvent(new ConversionLogEvent
            {
                JobId = jobId,
                EventType = ConversionEventType.ConversionCompleted,
                JobStatus = ConversionStatus.Converting,
                Message = $"Конвертация завершена, размер MP3: {FormatBytes(fileSizeBytes)}, длительность: {TimeSpan.FromSeconds(durationSeconds):hh\\:mm\\:ss}",
                Details = path != null ? $"Сохранено в: {path}" : null,
                FileSizeBytes = fileSizeBytes,
                DurationSeconds = durationSeconds
            });
            
            await Task.CompletedTask;
        }

        public async Task LogUploadStartedAsync(string jobId, long queueTimeMs, long fileSizeBytes)
        {
            EnqueueLogEvent(new ConversionLogEvent
            {
                JobId = jobId,
                EventType = ConversionEventType.UploadStarted,
                JobStatus = ConversionStatus.Uploading,
                Message = "Начата загрузка MP3 в хранилище",
                FileSizeBytes = fileSizeBytes,
                QueueTimeMs = queueTimeMs
            });
            
            await Task.CompletedTask;
        }

        public async Task LogUploadProgressAsync(string jobId, double percent, long bytesSent)
        {
            // Progress логи не пишем в БД
            await Task.CompletedTask;
        }

        public async Task LogUploadCompletedAsync(string jobId, string mp3Url)
        {
            EnqueueLogEvent(new ConversionLogEvent
            {
                JobId = jobId,
                EventType = ConversionEventType.UploadCompleted,
                JobStatus = ConversionStatus.Uploading,
                Message = "Загрузка в хранилище завершена",
                Details = $"MP3 URL: {mp3Url}"
            });
            
            await Task.CompletedTask;
        }

        public async Task LogJobCompletedAsync(string jobId, string mp3Url, long totalTimeMs)
        {
            if (_jobTimers.TryRemove(jobId, out var stopwatch))
            {
                stopwatch.Stop();
            }
            
            EnqueueLogEvent(new ConversionLogEvent
            {
                JobId = jobId,
                EventType = ConversionEventType.JobCompleted,
                JobStatus = ConversionStatus.Completed,
                Message = $"Задача успешно завершена за {TimeSpan.FromMilliseconds(totalTimeMs):hh\\:mm\\:ss\\.fff}",
                Details = $"MP3 URL: {mp3Url}",
                QueueTimeMs = totalTimeMs
            });
            
            await Task.CompletedTask;
        }

        public async Task LogErrorAsync(string jobId, string errorMessage, string? stackTrace = null, ConversionStatus status = ConversionStatus.Failed)
        {
            EnqueueLogEvent(new ConversionLogEvent
            {
                JobId = jobId,
                EventType = ConversionEventType.Error,
                JobStatus = status,
                Message = errorMessage,
                Details = stackTrace
            });
            
            // При ошибке сразу сбрасываем логи (не ждём таймера)
            await FlushLogsAsync();
        }

        public async Task LogWarningAsync(string jobId, string message, string? details = null)
        {
            EnqueueLogEvent(new ConversionLogEvent
            {
                JobId = jobId,
                EventType = ConversionEventType.Warning,
                JobStatus = ConversionStatus.Pending,
                Message = message,
                Details = details
            });
            
            await Task.CompletedTask;
        }

        public async Task LogCacheHitAsync(string jobId, string mp3Url, string videoHash)
        {
            EnqueueLogEvent(new ConversionLogEvent
            {
                JobId = jobId,
                EventType = ConversionEventType.CacheHit,
                JobStatus = ConversionStatus.Completed,
                Message = $"Найден кэшированный результат (хеш: {videoHash})",
                Details = $"MP3 URL: {mp3Url}"
            });
            
            await Task.CompletedTask;
        }

        public async Task LogJobRecoveredAsync(string jobId, ConversionStatus previousStatus, ConversionStatus newStatus, string reason)
        {
            EnqueueLogEvent(new ConversionLogEvent
            {
                JobId = jobId,
                EventType = ConversionEventType.JobRecovered,
                JobStatus = newStatus,
                Message = $"Задача восстановлена с {previousStatus} на {newStatus}",
                Details = reason
            });
            
            await Task.CompletedTask;
        }

        public async Task LogJobCancelledAsync(string jobId, string reason)
        {
            EnqueueLogEvent(new ConversionLogEvent
            {
                JobId = jobId,
                EventType = ConversionEventType.JobCancelled,
                JobStatus = ConversionStatus.Failed,
                Message = "Задача отменена",
                Details = reason
            });
            
            await Task.CompletedTask;
        }

        public async Task LogJobDelayedAsync(string jobId, string reason, long delayMs)
        {
            EnqueueLogEvent(new ConversionLogEvent
            {
                JobId = jobId,
                EventType = ConversionEventType.JobDelayed,
                JobStatus = ConversionStatus.Pending,
                Message = $"Задача отложена на {delayMs}мс",
                Details = reason
            });
            
            await Task.CompletedTask;
        }

        public async Task LogJobRetryAsync(string jobId, int attemptNumber, string reason)
        {
            EnqueueLogEvent(new ConversionLogEvent
            {
                JobId = jobId,
                EventType = ConversionEventType.JobRetry,
                JobStatus = ConversionStatus.Pending,
                Message = $"Повторная попытка #{attemptNumber}",
                Details = reason,
                Step = attemptNumber
            });
            
            await Task.CompletedTask;
        }

        public async Task LogSystemInfoAsync(string message, string? details = null)
        {
            // Системные логи НЕ пишем в БД (только в консоль через стандартный ILogger)
            await Task.CompletedTask;
        }

        private static string FormatBytes(long bytes)
        {
            string[] sizes = { "B", "KB", "MB", "GB", "TB" };
            double len = bytes;
            int order = 0;
            while (len >= 1024 && order < sizes.Length - 1)
            {
                order++;
                len = len / 1024;
            }
            return $"{len:0.##} {sizes[order]}";
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            
            // Сбрасываем все оставшиеся логи перед завершением
            FlushLogsAsync().GetAwaiter().GetResult();
            
            _flushTimer?.Dispose();
            _flushSemaphore?.Dispose();
        }
    }
}

