using FileConverter.Data;
using FileConverter.Models;
using FileConverter.Services.Interfaces;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using System;
using System.Linq;
using System.Threading.Tasks;
using System.Text;
using Microsoft.EntityFrameworkCore;

using FileConverter.Services;

namespace FileConverter.Services
{
    /// <summary>
    /// Сервис для восстановления "застрявших" заданий.
    /// Передает восстановленные задачи в VideoConverter для интеллектуальной обработки.
    /// </summary>
    public class JobRecoveryService : IJobRecoveryService
    {
        private readonly IJobRepository _jobRepository;
        private readonly IConversionLogRepository _logRepository;
        private readonly ILogger<JobRecoveryService> _logger;
        private readonly IConversionLogger _conversionLogger;
        private readonly IConfiguration _configuration;
        private readonly IVideoConverter _videoConverter;
        
        // Настройки восстановления заданий
        private readonly int _staleJobThresholdMinutes;
        private readonly int _maxAttempts;
        
        /// <summary>
        /// Инициализирует новый экземпляр сервиса восстановления заданий
        /// </summary>
        public JobRecoveryService(
            IJobRepository jobRepository,
            IConversionLogRepository logRepository,
            ILogger<JobRecoveryService> logger,
            IConversionLogger conversionLogger,
            IConfiguration configuration,
            IVideoConverter videoConverter)
        {
            _jobRepository = jobRepository;
            _logRepository = logRepository;
            _logger = logger;
            _conversionLogger = conversionLogger;
            _configuration = configuration;
            _videoConverter = videoConverter;
            
            // Загружаем настройки
            _staleJobThresholdMinutes = _configuration.GetValue<int>("Performance:StaleJobThresholdMinutes", 30);
            _maxAttempts = _configuration.GetValue<int>("Performance:JobRetryLimit", 3);
            // Логирование инициализации убрано для уменьшения количества логов
        }
        
        /// <inheritdoc/>
        public async Task<int> RecoverStaleJobsAsync()
        {
            int recoveredCount = 0;
            try
            {
                // Логирование запуска процесса убрано для уменьшения количества логов
                
                // Добавляем диагностику состояния задач
                await LogTaskStatisticsAsync();
                
                var recoveryTimeSpan = TimeSpan.FromMinutes(_staleJobThresholdMinutes);
                var staleJobs = await _jobRepository.GetStaleJobsAsync(recoveryTimeSpan);
                
                if (!staleJobs.Any())
                {
                    // Логирование отсутствия зависших заданий убрано (это нормальная ситуация)
                    return 0;
                }
                
                _logger.LogWarning("Найдено {Count} зависших заданий (не обновлялись дольше {Minutes} мин).", 
                    staleJobs.Count, _staleJobThresholdMinutes);
                
                // Подробная диагностика зависших задач
                foreach (var staleJob in staleJobs)
                {
                    var timeStale = DateTime.UtcNow - (staleJob.LastAttemptAt ?? staleJob.CreatedAt);
                    _logger.LogWarning("Зависшая задача {JobId}: Статус {Status}, Попытка {Attempt}/{MaxAttempts}, " +
                                      "Висит {StaleMinutes:F1} мин, URL: {VideoUrl}", 
                        staleJob.Id, staleJob.Status, staleJob.ProcessingAttempts, _maxAttempts, 
                        timeStale.TotalMinutes, staleJob.VideoUrl);
                    
                    await _conversionLogger.LogSystemInfoAsync(
                        $"Обнаружена зависшая задача {staleJob.Id} в статусе {staleJob.Status} (висит {timeStale.TotalMinutes:F1} мин)");
                }
                
                foreach (var job in staleJobs)
                {
                     _logger.LogWarning("Попытка восстановления зависшей задачи {JobId} из статуса {Status} (Попытка {Attempt}/{MaxAttempts})", 
                        job.Id, job.Status, job.ProcessingAttempts + 1, _maxAttempts);
                    try
                    {
                        // Используем переименованный метод RecoverSingleJobAsync
                        bool recovered = await RecoverSingleJobAsync(job);
                        if (recovered)
                        {
                            recoveredCount++;
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Ошибка при восстановлении задачи {JobId}: {Message}", job.Id, ex.Message);
                        var detailed = BuildExceptionDetails(ex);
                        await _conversionLogger.LogErrorAsync(job.Id, $"Ошибка восстановления: {ex.Message}", detailed, job.Status);
                    }
                }
                 
                if(recoveredCount > 0) 
                {
                    await _conversionLogger.LogSystemInfoAsync(
                        $"Восстановлено {recoveredCount} из {staleJobs.Count} зависших заданий.");
                     _logger.LogInformation("Восстановлено {RecoveredCount} зависших заданий.", recoveredCount);
                }
                 else 
                 {
                     _logger.LogInformation("Не удалось восстановить ни одно из {StaleJobsCount} зависших заданий (возможно, все достигли лимита попыток).", staleJobs.Count);
                 }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ошибка в процессе восстановления зависших заданий: {Message}", ex.Message);
                await _conversionLogger.LogSystemInfoAsync($"Критическая ошибка сервиса восстановления: {ex.Message}");
            }
            return recoveredCount;
        }

        /// <summary>
        /// Формирует развернутые детали исключения, включая вложенные исключения и данные EF Core
        /// </summary>
        private static string BuildExceptionDetails(Exception exception)
        {
            var builder = new StringBuilder();

            void AppendException(Exception ex, int level)
            {
                var indent = new string('>', level);
                builder.AppendLine($"{indent} Exception: {ex.GetType().FullName}");
                builder.AppendLine($"{indent} Message: {ex.Message}");
                if (!string.IsNullOrWhiteSpace(ex.StackTrace))
                {
                    builder.AppendLine($"{indent} StackTrace:");
                    builder.AppendLine(ex.StackTrace);
                }

                if (ex is DbUpdateException dbUpdateEx)
                {
                    builder.AppendLine($"{indent} DbUpdateException entries:");
                    foreach (var entry in dbUpdateEx.Entries)
                    {
                        try
                        {
                            builder.AppendLine($"{indent}  Entity: {entry.Entity.GetType().FullName}, State: {entry.State}");
                            foreach (var prop in entry.Properties)
                            {
                                builder.AppendLine($"{indent}   - {prop.Metadata.Name} = {prop.CurrentValue}");
                            }
                        }
                        catch
                        {
                        }
                    }

                    if (dbUpdateEx.InnerException != null)
                    {
                        builder.AppendLine($"{indent} DbUpdateException.Inner: {dbUpdateEx.InnerException.GetType().FullName}: {dbUpdateEx.InnerException.Message}");
                    }
                }

                if (ex.InnerException != null)
                {
                    AppendException(ex.InnerException, level + 1);
                }
            }

            AppendException(exception, 0);
            return builder.ToString();
        }
        
        /// <summary>
        /// Восстанавливает конкретное "застрявшее" задание: проверяет лимит попыток,
        /// сбрасывает статус на Pending и добавляет в очередь обработки.
        /// </summary>
        /// <param name="job">Задание для восстановления</param>
        /// <returns>true, если задание успешно поставлено в очередь на повторную обработку или отмечено как Failed</returns>
        // Переименовано из RecoverJobAsync
        private async Task<bool> RecoverSingleJobAsync(ConversionJob job)
        {
            var previousStatus = job.Status; // Запоминаем предыдущий статус для логов

            // Если превышено количество попыток - отмечаем как Failed
            if (job.ProcessingAttempts >= _maxAttempts)
            {
                 _logger.LogWarning("Задача {JobId}: Превышено максимальное количество попыток ({MaxAttempts}). Задача будет отмечена как Failed.", job.Id, _maxAttempts);
                await _conversionLogger.LogJobCancelledAsync(job.Id, 
                    $"Превышено максимальное количество попыток ({_maxAttempts}) при восстановлении");
                
                 // Обновляем статус через репозиторий
                job.Status = ConversionStatus.Failed;
                job.ErrorMessage = $"Превышено максимальное количество попыток ({_maxAttempts}) при восстановлении";
                job.CompletedAt = DateTime.UtcNow;
                job.LastAttemptAt = DateTime.UtcNow; // Обновим время последней попытки
                await _jobRepository.UpdateJobAsync(job);
                
                return true; // Считаем успешным восстановлением (перевод в Failed)
            }
            
            // Сбрасываем статус до Pending для повторного запуска
            job.Status = ConversionStatus.Pending;
            job.ProcessingAttempts++; // Увеличиваем счетчик попыток
            job.LastAttemptAt = DateTime.UtcNow; // Обновляем время попытки
            job.ErrorMessage = null; // Очищаем предыдущую ошибку, если была
            
            await _jobRepository.UpdateJobAsync(job); // Сохраняем изменения (Pending, Attempts, LastAttempt)
            
             _logger.LogInformation("Задача {JobId}: Статус сброшен на Pending (Попытка {Attempt}/{MaxAttempts}).", job.Id, job.ProcessingAttempts, _maxAttempts);

            // Логируем восстановление задания
            var reason = $"Задание не обновлялось более {_staleJobThresholdMinutes} минут в статусе {previousStatus}";
            await _conversionLogger.LogJobRecoveredAsync(
                job.Id, previousStatus, job.Status, reason);
            
            // Повторно запускаем обработку через VideoConverter (с проверкой кэша и валидацией)
            try
            {
                await _videoConverter.ProcessVideo(job.Id);
                _logger.LogInformation("Восстановленная задача {JobId} передана в VideoConverter для обработки.", job.Id);
                await _conversionLogger.LogSystemInfoAsync($"Восстановленная задача {job.Id} передана в VideoConverter для обработки.");
                return true; // Успешно передано в VideoConverter
            }
            catch (Exception ex)
            {
                 _logger.LogError(ex, "Не удалось обработать восстановленную задачу {JobId} через VideoConverter.", job.Id);
                 await _conversionLogger.LogErrorAsync(job.Id, $"Ошибка при обработке восстановленной задачи через VideoConverter: {ex.Message}", ex.StackTrace, ConversionStatus.Pending);
                 return false; // Не удалось обработать
            }
        }
        
        // Время последнего логирования статистики
        private DateTime _lastStatisticsLogTime = DateTime.MinValue;
        
        /// <summary>
        /// Логирует статистику по задачам для диагностики (не чаще чем раз в 10 минут)
        /// </summary>
        private async Task LogTaskStatisticsAsync()
        {
            try
            {
                // Логируем статистику не чаще чем раз в 10 минут
                var timeSinceLastLog = DateTime.UtcNow - _lastStatisticsLogTime;
                if (timeSinceLastLog.TotalMinutes < 10)
                {
                    return; // Пропускаем логирование, если прошло меньше 10 минут
                }
                
                var downloadingCount = await _jobRepository.GetJobsByStatusesCountAsync(new[] { ConversionStatus.Downloading });
                var convertingCount = await _jobRepository.GetJobsByStatusesCountAsync(new[] { ConversionStatus.Converting });
                var uploadingCount = await _jobRepository.GetJobsByStatusesCountAsync(new[] { ConversionStatus.Uploading });
                var pendingCount = await _jobRepository.GetJobsByStatusesCountAsync(new[] { ConversionStatus.Pending });
                var completedCount = await _jobRepository.GetJobsByStatusesCountAsync(new[] { ConversionStatus.Completed });
                var failedCount = await _jobRepository.GetJobsByStatusesCountAsync(new[] { ConversionStatus.Failed });
                
                // Логирование в консоль убрано для уменьшения количества логов
                
                await _conversionLogger.LogSystemInfoAsync(
                    $"Статистика задач: Pending={pendingCount}, Downloading={downloadingCount}, Converting={convertingCount}, " +
                    $"Uploading={uploadingCount}, Completed={completedCount}, Failed={failedCount}");
                
                // Обновляем время последнего логирования
                _lastStatisticsLogTime = DateTime.UtcNow;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ошибка при получении статистики задач");
            }
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