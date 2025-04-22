using FileConverter.Data;
using FileConverter.Models;
using FileConverter.Services.Interfaces;
using System.Collections.Concurrent;
using System.Diagnostics;

namespace FileConverter.Services
{
    /// <summary>
    /// Сервис логирования процесса конвертации
    /// </summary>
    public class ConversionLogger : IConversionLogger
    {
        private readonly IConversionLogRepository _logRepository;
        private readonly ILogger<ConversionLogger> _logger;
        private readonly IJobRepository _jobRepository;
        
        // Отслеживание времени начала задач
        private readonly ConcurrentDictionary<string, Stopwatch> _jobTimers = new();
        
        // Отслеживание времени начала стадий
        private readonly ConcurrentDictionary<string, (DateTime Start, ConversionStatus Status)> _stageTimers = new();
        
        /// <summary>
        /// Инициализирует новый экземпляр сервиса логирования
        /// </summary>
        public ConversionLogger(
            IConversionLogRepository logRepository,
            ILogger<ConversionLogger> logger,
            IJobRepository jobRepository)
        {
            _logRepository = logRepository;
            _logger = logger;
            _jobRepository = jobRepository;
        }
        
        /// <inheritdoc/>
        public async Task LogJobCreatedAsync(string jobId, string videoUrl, string? batchId = null)
        {
            // Запускаем таймер для задачи
            var stopwatch = new Stopwatch();
            stopwatch.Start();
            _jobTimers[jobId] = stopwatch;
            
            await CreateAndSaveLogEventAsync(new ConversionLogEvent
            {
                JobId = jobId,
                BatchId = batchId,
                EventType = ConversionEventType.JobCreated,
                JobStatus = ConversionStatus.Pending,
                Message = $"Задача создана для видео {videoUrl}",
                VideoUrl = videoUrl
            });
            
            _logger.LogInformation("Задача {JobId} создана для {VideoUrl}", jobId, videoUrl);
        }
        
        /// <inheritdoc/>
        public async Task LogJobQueuedAsync(string jobId, string videoUrl, string? details = null)
        {
            await CreateAndSaveLogEventAsync(new ConversionLogEvent
            {
                JobId = jobId,
                EventType = ConversionEventType.JobQueued,
                JobStatus = ConversionStatus.Pending,
                Message = $"Задача поставлена в очередь конвертации",
                VideoUrl = videoUrl,
                Details = details
            });
            
            _logger.LogInformation("Задача {JobId} поставлена в очередь", jobId);
        }
        
        /// <inheritdoc/>
        public async Task LogStatusChangedAsync(string jobId, ConversionStatus status, string? details = null, int attemptNumber = 1)
        {
            // Запоминаем время начала стадии
            _stageTimers[jobId] = (DateTime.UtcNow, status);
            
            await CreateAndSaveLogEventAsync(new ConversionLogEvent
            {
                JobId = jobId,
                EventType = ConversionEventType.StatusChanged,
                JobStatus = status,
                Message = $"Статус изменен на {status}",
                Details = details,
                AttemptNumber = attemptNumber
            });
            
            _logger.LogInformation("Задача {JobId} перешла в статус {Status} (попытка #{AttemptNumber})", 
                jobId, status, attemptNumber);
        }
        
        /// <inheritdoc/>
        public async Task LogDownloadStartedAsync(string jobId, string videoUrl, long queueTimeMs)
        {
            await CreateAndSaveLogEventAsync(new ConversionLogEvent
            {
                JobId = jobId,
                EventType = ConversionEventType.DownloadStarted,
                JobStatus = ConversionStatus.Downloading,
                Message = $"Начата загрузка видео",
                VideoUrl = videoUrl,
                QueueTimeMs = queueTimeMs
            });
            
            _logger.LogInformation("Задача {JobId}: Начата загрузка видео (время в очереди: {QueueTime}мс)", 
                jobId, queueTimeMs);
        }
        
        /// <inheritdoc/>
        public async Task LogDownloadProgressAsync(string jobId, long bytesReceived, long? totalBytes = null, double? rateBytes = null)
        {
            var percentStr = totalBytes.HasValue ? $" ({(double)bytesReceived / totalBytes.Value * 100:F1}%)" : "";
            var rateStr = rateBytes.HasValue ? $", скорость: {FormatBytesPerSecond(rateBytes.Value)}" : "";
            
            await CreateAndSaveLogEventAsync(new ConversionLogEvent
            {
                JobId = jobId,
                EventType = ConversionEventType.DownloadProgress,
                JobStatus = ConversionStatus.Downloading,
                Message = $"Прогресс загрузки: {FormatBytes(bytesReceived)}{percentStr}{rateStr}",
                FileSizeBytes = bytesReceived,
                ProcessingRateBytesPerSecond = rateBytes
            });
            
            // Не логируем в консоль каждое обновление прогресса, чтобы не перегружать логи
        }
        
        /// <inheritdoc/>
        public async Task LogDownloadCompletedAsync(string jobId, long fileSizeBytes, string? path = null)
        {
            await CreateAndSaveLogEventAsync(new ConversionLogEvent
            {
                JobId = jobId,
                EventType = ConversionEventType.DownloadCompleted,
                JobStatus = ConversionStatus.Downloading,
                Message = $"Загрузка видео завершена, размер: {FormatBytes(fileSizeBytes)}",
                Details = path != null ? $"Сохранено в: {path}" : null,
                FileSizeBytes = fileSizeBytes
            });
            
            // Рассчитываем время стадии, если есть
            if (_stageTimers.TryGetValue(jobId, out var stageInfo) && stageInfo.Status == ConversionStatus.Downloading)
            {
                var duration = DateTime.UtcNow - stageInfo.Start;
                _logger.LogInformation("Задача {JobId}: Загрузка видео завершена за {Duration:c}, размер: {Size}", 
                    jobId, duration, FormatBytes(fileSizeBytes));
            }
            else
            {
                _logger.LogInformation("Задача {JobId}: Загрузка видео завершена, размер: {Size}", 
                    jobId, FormatBytes(fileSizeBytes));
            }
        }
        
        /// <inheritdoc/>
        public async Task LogConversionStartedAsync(string jobId, long queueTimeMs, string? details = null)
        {
            await CreateAndSaveLogEventAsync(new ConversionLogEvent
            {
                JobId = jobId,
                EventType = ConversionEventType.ConversionStarted,
                JobStatus = ConversionStatus.Converting,
                Message = $"Начата конвертация видео в MP3",
                Details = details,
                QueueTimeMs = queueTimeMs
            });
            
            _logger.LogInformation("Задача {JobId}: Начата конвертация (время в очереди: {QueueTime}мс)", 
                jobId, queueTimeMs);
        }
        
        /// <inheritdoc/>
        public async Task LogConversionProgressAsync(string jobId, double percent, double? timeRemainingSeconds = null)
        {
            var timeStr = timeRemainingSeconds.HasValue 
                ? $", осталось: {TimeSpan.FromSeconds(timeRemainingSeconds.Value):hh\\:mm\\:ss}" 
                : "";
            
            await CreateAndSaveLogEventAsync(new ConversionLogEvent
            {
                JobId = jobId,
                EventType = ConversionEventType.ConversionProgress,
                JobStatus = ConversionStatus.Converting,
                Message = $"Прогресс конвертации: {percent:F1}%{timeStr}",
                Step = (int)percent,
                TotalSteps = 100,
                DurationSeconds = timeRemainingSeconds
            });
            
            // Не логируем в консоль каждое обновление прогресса, чтобы не перегружать логи
        }
        
        /// <inheritdoc/>
        public async Task LogConversionCompletedAsync(string jobId, long fileSizeBytes, double durationSeconds, string? path = null)
        {
            await CreateAndSaveLogEventAsync(new ConversionLogEvent
            {
                JobId = jobId,
                EventType = ConversionEventType.ConversionCompleted,
                JobStatus = ConversionStatus.Converting,
                Message = $"Конвертация завершена, размер MP3: {FormatBytes(fileSizeBytes)}, длительность: {TimeSpan.FromSeconds(durationSeconds):hh\\:mm\\:ss}",
                Details = path != null ? $"Сохранено в: {path}" : null,
                FileSizeBytes = fileSizeBytes,
                DurationSeconds = durationSeconds
            });
            
            // Рассчитываем время стадии, если есть
            if (_stageTimers.TryGetValue(jobId, out var stageInfo) && stageInfo.Status == ConversionStatus.Converting)
            {
                var duration = DateTime.UtcNow - stageInfo.Start;
                _logger.LogInformation("Задача {JobId}: Конвертация завершена за {Duration:c}, размер MP3: {Size}, длительность: {AudioLength:c}", 
                    jobId, duration, FormatBytes(fileSizeBytes), TimeSpan.FromSeconds(durationSeconds));
            }
            else
            {
                _logger.LogInformation("Задача {JobId}: Конвертация завершена, размер MP3: {Size}, длительность: {AudioLength:c}", 
                    jobId, FormatBytes(fileSizeBytes), TimeSpan.FromSeconds(durationSeconds));
            }
        }
        
        /// <inheritdoc/>
        public async Task LogUploadStartedAsync(string jobId, long queueTimeMs, long fileSizeBytes)
        {
            await CreateAndSaveLogEventAsync(new ConversionLogEvent
            {
                JobId = jobId,
                EventType = ConversionEventType.UploadStarted,
                JobStatus = ConversionStatus.Uploading,
                Message = $"Начата загрузка MP3 в хранилище",
                FileSizeBytes = fileSizeBytes,
                QueueTimeMs = queueTimeMs
            });
            
            _logger.LogInformation("Задача {JobId}: Начата загрузка MP3 в хранилище (время в очереди: {QueueTime}мс)", 
                jobId, queueTimeMs);
        }
        
        /// <inheritdoc/>
        public async Task LogUploadProgressAsync(string jobId, double percent, long bytesSent)
        {
            await CreateAndSaveLogEventAsync(new ConversionLogEvent
            {
                JobId = jobId,
                EventType = ConversionEventType.UploadProgress,
                JobStatus = ConversionStatus.Uploading,
                Message = $"Прогресс загрузки в хранилище: {percent:F1}%, отправлено {FormatBytes(bytesSent)}",
                FileSizeBytes = bytesSent,
                Step = (int)percent,
                TotalSteps = 100
            });
            
            // Не логируем в консоль каждое обновление прогресса, чтобы не перегружать логи
        }
        
        /// <inheritdoc/>
        public async Task LogUploadCompletedAsync(string jobId, string mp3Url)
        {
            await CreateAndSaveLogEventAsync(new ConversionLogEvent
            {
                JobId = jobId,
                EventType = ConversionEventType.UploadCompleted,
                JobStatus = ConversionStatus.Uploading,
                Message = $"Загрузка MP3 в хранилище завершена",
                Mp3Url = mp3Url
            });
            
            // Рассчитываем время стадии, если есть
            if (_stageTimers.TryGetValue(jobId, out var stageInfo) && stageInfo.Status == ConversionStatus.Uploading)
            {
                var duration = DateTime.UtcNow - stageInfo.Start;
                _logger.LogInformation("Задача {JobId}: Загрузка MP3 в хранилище завершена за {Duration:c}", 
                    jobId, duration);
            }
            else
            {
                _logger.LogInformation("Задача {JobId}: Загрузка MP3 в хранилище завершена", jobId);
            }
        }
        
        /// <inheritdoc/>
        public async Task LogJobCompletedAsync(string jobId, string mp3Url, long totalTimeMs)
        {
            // Останавливаем таймер задачи
            if (_jobTimers.TryRemove(jobId, out var stopwatch))
            {
                stopwatch.Stop();
                totalTimeMs = stopwatch.ElapsedMilliseconds;
            }
            
            await CreateAndSaveLogEventAsync(new ConversionLogEvent
            {
                JobId = jobId,
                EventType = ConversionEventType.JobCompleted,
                JobStatus = ConversionStatus.Completed,
                Message = $"Задача успешно завершена, общее время: {TimeSpan.FromMilliseconds(totalTimeMs):hh\\:mm\\:ss\\.fff}",
                Mp3Url = mp3Url
            });
            
            _logger.LogInformation("Задача {JobId}: Выполнена успешно за {TotalTime:c}, MP3: {Mp3Url}", 
                jobId, TimeSpan.FromMilliseconds(totalTimeMs), mp3Url);
            
            // Очищаем таймер стадии
            _stageTimers.TryRemove(jobId, out _);
        }
        
        /// <inheritdoc/>
        public async Task LogErrorAsync(string jobId, string errorMessage, string? stackTrace = null, ConversionStatus status = ConversionStatus.Failed)
        {
            await CreateAndSaveLogEventAsync(new ConversionLogEvent
            {
                JobId = jobId,
                EventType = ConversionEventType.Error,
                JobStatus = status,
                Message = $"Ошибка: {errorMessage}",
                ErrorMessage = errorMessage,
                ErrorStackTrace = stackTrace
            });
            
            _logger.LogError("Задача {JobId}: Ошибка: {ErrorMessage}", jobId, errorMessage);
            
            // Останавливаем таймер задачи при финальной ошибке
            if (status == ConversionStatus.Failed)
            {
                _jobTimers.TryRemove(jobId, out _);
                _stageTimers.TryRemove(jobId, out _);
            }
        }
        
        /// <inheritdoc/>
        public async Task LogWarningAsync(string jobId, string message, string? details = null)
        {
            await CreateAndSaveLogEventAsync(new ConversionLogEvent
            {
                JobId = jobId,
                EventType = ConversionEventType.Warning,
                JobStatus = await GetJobStatusAsync(jobId),
                Message = $"Предупреждение: {message}",
                Details = details
            });
            
            _logger.LogWarning("Задача {JobId}: Предупреждение: {Message}", jobId, message);
        }
        
        /// <inheritdoc/>
        public async Task LogCacheHitAsync(string jobId, string mp3Url, string videoHash)
        {
            await CreateAndSaveLogEventAsync(new ConversionLogEvent
            {
                JobId = jobId,
                EventType = ConversionEventType.CacheHit,
                JobStatus = ConversionStatus.Completed,
                Message = $"Найден готовый результат конвертации в репозитории",
                Details = $"Хеш видео: {videoHash}",
                Mp3Url = mp3Url
            });
            
            _logger.LogInformation("Задача {JobId}: Найден готовый результат конвертации, MP3: {Mp3Url}", 
                jobId, mp3Url);
            
            // Очищаем таймеры, так как задача завершена
            _jobTimers.TryRemove(jobId, out _);
            _stageTimers.TryRemove(jobId, out _);
        }
        
        /// <inheritdoc/>
        public async Task LogJobRecoveredAsync(string jobId, ConversionStatus previousStatus, ConversionStatus newStatus, string reason)
        {
            await CreateAndSaveLogEventAsync(new ConversionLogEvent
            {
                JobId = jobId,
                EventType = ConversionEventType.JobRecovered,
                JobStatus = newStatus,
                Message = $"Задача восстановлена из статуса {previousStatus} в статус {newStatus}",
                Details = $"Причина: {reason}"
            });
            
            _logger.LogWarning("Задача {JobId}: Восстановлена из {PreviousStatus} в {NewStatus}. Причина: {Reason}", 
                jobId, previousStatus, newStatus, reason);
        }
        
        /// <inheritdoc/>
        public async Task LogJobCancelledAsync(string jobId, string reason)
        {
            await CreateAndSaveLogEventAsync(new ConversionLogEvent
            {
                JobId = jobId,
                EventType = ConversionEventType.JobCancelled,
                JobStatus = ConversionStatus.Failed,
                Message = $"Задача отменена",
                Details = $"Причина: {reason}"
            });
            
            _logger.LogWarning("Задача {JobId}: Отменена. Причина: {Reason}", jobId, reason);
            
            // Очищаем таймеры
            _jobTimers.TryRemove(jobId, out _);
            _stageTimers.TryRemove(jobId, out _);
        }
        
        /// <inheritdoc/>
        public async Task LogJobDelayedAsync(string jobId, string reason, long delayMs)
        {
            await CreateAndSaveLogEventAsync(new ConversionLogEvent
            {
                JobId = jobId,
                EventType = ConversionEventType.JobDelayed,
                JobStatus = await GetJobStatusAsync(jobId),
                Message = $"Задача отложена на {TimeSpan.FromMilliseconds(delayMs):hh\\:mm\\:ss\\.fff}",
                Details = $"Причина: {reason}",
                WaitReason = reason
            });
            
            _logger.LogInformation("Задача {JobId}: Отложена на {Delay:c}. Причина: {Reason}", 
                jobId, TimeSpan.FromMilliseconds(delayMs), reason);
        }
        
        /// <inheritdoc/>
        public async Task LogJobRetryAsync(string jobId, int attemptNumber, string reason)
        {
            await CreateAndSaveLogEventAsync(new ConversionLogEvent
            {
                JobId = jobId,
                EventType = ConversionEventType.JobRetry,
                JobStatus = ConversionStatus.Pending,
                Message = $"Повторная попытка #{attemptNumber}",
                Details = $"Причина: {reason}",
                AttemptNumber = attemptNumber
            });
            
            _logger.LogWarning("Задача {JobId}: Повторная попытка #{AttemptNumber}. Причина: {Reason}", 
                jobId, attemptNumber, reason);
        }
        
        /// <inheritdoc/>
        public async Task LogSystemInfoAsync(string message, string? details = null)
        {
            await CreateAndSaveLogEventAsync(new ConversionLogEvent
            {
                JobId = "SYSTEM",  // Системные события не имеют JobId
                EventType = ConversionEventType.SystemInfo,
                JobStatus = ConversionStatus.Pending,  // Не имеет значения для системных событий
                Message = message,
                Details = details
            });
            
            _logger.LogInformation("СИСТЕМА: {Message}", message);
        }
        
        /// <summary>
        /// Создает и сохраняет запись лога в репозитории
        /// </summary>
        private async Task<ConversionLogEvent> CreateAndSaveLogEventAsync(ConversionLogEvent logEvent)
        {
            try
            {
                // Проверяем, если не задан статус задачи, получаем его из БД
                if (logEvent.EventType != ConversionEventType.SystemInfo && 
                    logEvent.JobStatus == ConversionStatus.Pending && 
                    logEvent.EventType != ConversionEventType.JobCreated &&
                    logEvent.EventType != ConversionEventType.JobQueued)
                {
                    logEvent.JobStatus = await GetJobStatusAsync(logEvent.JobId);
                }
                
                // Добавляем информацию о BatchId, если она не указана
                if (string.IsNullOrEmpty(logEvent.BatchId) && 
                    logEvent.EventType != ConversionEventType.SystemInfo)
                {
                    var job = await _jobRepository.GetJobByIdAsync(logEvent.JobId);
                    if (job != null && !string.IsNullOrEmpty(job.BatchId))
                    {
                        logEvent.BatchId = job.BatchId;
                    }
                }
                
                return await _logRepository.AddLogAsync(logEvent);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ошибка при сохранении события лога для задачи {JobId}: {Message}", 
                    logEvent.JobId, ex.Message);
                    
                // В случае ошибки при записи лога, возвращаем исходное событие
                return logEvent;
            }
        }
        
        /// <summary>
        /// Получает текущий статус задачи
        /// </summary>
        private async Task<ConversionStatus> GetJobStatusAsync(string jobId)
        {
            try
            {
                var job = await _jobRepository.GetJobByIdAsync(jobId);
                return job?.Status ?? ConversionStatus.Pending;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ошибка при получении статуса задачи {JobId}: {Message}", 
                    jobId, ex.Message);
                return ConversionStatus.Pending;
            }
        }
        
        /// <summary>
        /// Форматирует размер в байтах в человекочитаемый вид
        /// </summary>
        private static string FormatBytes(long bytes)
        {
            string[] suffixes = { "Б", "КБ", "МБ", "ГБ", "ТБ" };
            int counter = 0;
            decimal number = bytes;
            
            while (Math.Round(number / 1024) >= 1)
            {
                number /= 1024;
                counter++;
            }
            
            return $"{number:F2} {suffixes[counter]}";
        }
        
        /// <summary>
        /// Форматирует скорость в байтах/сек в человекочитаемый вид
        /// </summary>
        private static string FormatBytesPerSecond(double bytesPerSecond)
        {
            return $"{FormatBytes((long)bytesPerSecond)}/с";
        }
    }
} 