using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FileConverter.Data;
using FileConverter.Models;
using FileConverter.Services.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using Xabe.FFmpeg;
using Xabe.FFmpeg.Events;
using Polly;

namespace FileConverter.Services
{
    /// <summary>
    /// Фоновый сервис для конвертации видео из очереди ConversionChannel.
    /// </summary>
    public class ConversionBackgroundService : BackgroundService
    {
        private readonly ILogger<ConversionBackgroundService> _logger;
        private readonly IServiceProvider _serviceProvider;
        private readonly ProcessingChannels _channels;
        private readonly MetricsCollector _metricsCollector;
        private readonly CpuThrottleService _cpuThrottleService;
        private readonly int _maxConcurrentConversions;

        public ConversionBackgroundService(
            ILogger<ConversionBackgroundService> logger,
            IServiceProvider serviceProvider,
            ProcessingChannels channels,
            MetricsCollector metricsCollector,
            CpuThrottleService cpuThrottleService,
            IConfiguration configuration)
        {
            _logger = logger;
            _serviceProvider = serviceProvider;
            _channels = channels;
            _metricsCollector = metricsCollector;
            _cpuThrottleService = cpuThrottleService;
            // Максимальное количество параллельных конвертаций, не больше чем ядер CPU - 1 (минимум 1)
            _maxConcurrentConversions = configuration.GetValue<int>("Performance:MaxConcurrentConversions", Math.Max(1, Environment.ProcessorCount - 1));
             _logger.LogInformation("ConversionBackgroundService инициализирован с {MaxConcurrentConversions} параллельными конвертациями.", _maxConcurrentConversions);
       }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("ConversionBackgroundService запущен.");
            
            // Инициализация FFmpeg (установка пути)
            var ffmpegPath = _serviceProvider.GetRequiredService<IConfiguration>().GetValue<string>("AppSettings:FFmpegPath");
            if (!string.IsNullOrEmpty(ffmpegPath))
            {
                FFmpeg.SetExecutablesPath(ffmpegPath);
                _logger.LogInformation("Путь к FFmpeg установлен: {FFmpegPath}", ffmpegPath);
            }
            else
            {
                 _logger.LogWarning("Путь к FFmpeg не указан в AppSettings:FFmpegPath. Используется путь по умолчанию.");
            }

            var tasks = new Task[_maxConcurrentConversions];
            for (int i = 0; i < _maxConcurrentConversions; i++)
            {
                tasks[i] = Task.Run(() => WorkerLoop(stoppingToken), stoppingToken);
            }

            await Task.WhenAll(tasks);

            _logger.LogInformation("ConversionBackgroundService остановлен.");
        }

        private async Task WorkerLoop(CancellationToken stoppingToken)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                string jobId = string.Empty;
                string videoPath = string.Empty; // Путь к скачанному видеофайлу
                string mp3Path = string.Empty;   // Путь к временному MP3 файлу
                string videoHash = string.Empty;

                try
                {
                    var item = await _channels.ConversionChannel.Reader.ReadAsync(stoppingToken);
                    jobId = item.JobId;
                    videoPath = item.VideoPath;
                    videoHash = item.VideoHash;

                    _logger.LogInformation("ConversionWorker получил задачу {JobId} (Видео: {VideoPath}, Хеш: {VideoHash})", jobId, videoPath, videoHash);

                    using (var scope = _serviceProvider.CreateScope())
                    {
                        var jobRepository = scope.ServiceProvider.GetRequiredService<IJobRepository>();
                        var conversionLogger = scope.ServiceProvider.GetRequiredService<IConversionLogger>();
                        var tempFileManager = scope.ServiceProvider.GetRequiredService<ITempFileManager>();
                        var logger = scope.ServiceProvider.GetRequiredService<ILogger<ConversionBackgroundService>>();
                        
                        DateTime queueStart = DateTime.UtcNow; // Время начала обработки из очереди
                        long queueTimeMs = 0;

                        try
                        {
                             var job = await jobRepository.GetJobByIdAsync(jobId);
                            if (job == null)
                            {
                                logger.LogWarning("Задача {JobId} не найдена в репозитории после извлечения из очереди конвертации.", jobId);
                                await conversionLogger.LogErrorAsync(jobId, $"Задача {jobId} не найдена в БД.");
                                // Важно удалить временный видеофайл, если он остался
                                CleanupFile(tempFileManager, videoPath, logger, jobId);
                                continue; 
                            }
                            
                            // Рассчитываем время ожидания в очереди конвертации (с момента последнего обновления статуса)
                            if (job.LastAttemptAt.HasValue)
                            {
                                queueTimeMs = (long)(DateTime.UtcNow - job.LastAttemptAt.Value).TotalMilliseconds;
                            }

                            await conversionLogger.LogConversionStartedAsync(jobId, queueTimeMs, $"Хеш видео: {videoHash}");

                            // Запускаем таймер для метрик конвертации
                            _metricsCollector.StartTimer("conversion_video_to_mp3", jobId);

                            // Обновляем статус на Converting
                            await DbJobManager.UpdateJobStatusAsync(jobRepository, jobId, ConversionStatus.Converting);
                            await conversionLogger.LogStatusChangedAsync(jobId, ConversionStatus.Converting);

                            // Создаем временный файл для MP3
                            mp3Path = tempFileManager.CreateTempFile(".mp3");
                            logger.LogInformation("Задача {JobId}: создан временный MP3 файл {Mp3Path}", jobId, mp3Path);

                            // Настройка Polly retry policy для FFmpeg конвертации
                            var retryPolicy = Policy
                                .Handle<Exception>(ex => !(ex is OperationCanceledException && stoppingToken.IsCancellationRequested))
                                .WaitAndRetryAsync(
                                    retryCount: 2, // Меньше попыток для конвертации, т.к. она более ресурсоемкая
                                    sleepDurationProvider: retryAttempt => TimeSpan.FromSeconds(5 * retryAttempt), // 5, 10 секунд
                                    onRetry: (outcome, timespan, retryCount, context) =>
                                    {
                                        logger.LogWarning("Задача {JobId}: Попытка {RetryCount}/2 конвертации неудачна. Повтор через {Delay}с. Ошибка: {Error}", 
                                            jobId, retryCount, timespan.TotalSeconds, outcome?.Message ?? "Unknown");
                                        
                                        // Очищаем частично созданный MP3 файл перед повтором
                                        if (!string.IsNullOrEmpty(mp3Path) && File.Exists(mp3Path))
                                        {
                                            try 
                                            { 
                                                File.Delete(mp3Path); 
                                                logger.LogDebug("Удален частично созданный MP3 файл: {Mp3Path}", mp3Path); 
                                            } 
                                            catch (Exception cleanupEx) 
                                            { 
                                                logger.LogWarning("Не удалось удалить MP3 файл {Mp3Path}: {Error}", mp3Path, cleanupEx.Message); 
                                            }
                                        }
                                        
                                        // Пересоздаем MP3 файл для следующей попытки
                                        mp3Path = tempFileManager.CreateTempFile(".mp3");
                                        logger.LogInformation("Задача {JobId}: пересоздан MP3 файл для повторной попытки: {Mp3Path}", jobId, mp3Path);
                                    });

                            // Выполняем конвертацию с retry
                            await retryPolicy.ExecuteAsync(async () =>
                            {
                                // Проверяем загрузку CPU перед началом конвертации
                                await _cpuThrottleService.WaitIfNeededAsync(stoppingToken);
                                
                                logger.LogInformation("Задача {JobId}: начинаем попытку FFmpeg конвертации", jobId);
                                
                                // Получаем информацию о медиафайле
                                logger.LogDebug("Задача {JobId}: получение информации о медиафайле {VideoPath}", jobId, videoPath);
                                IMediaInfo mediaInfo = await FFmpeg.GetMediaInfo(videoPath, stoppingToken);
                                logger.LogDebug("Задача {JobId}: информация получена, длительность {Duration}", jobId, mediaInfo.Duration);

                                if (mediaInfo.AudioStreams?.Any() != true)
                                {
                                    throw new InvalidOperationException("Аудиопоток не найден в видеофайле.");
                                }
                                
                                // Обновляем задачу информацией о временных путях (на случай восстановления)
                                // Эта информация не сохраняется в БД через атрибут [NotMapped]
                                job.TempVideoPath = videoPath; 
                                job.TempMp3Path = mp3Path; 
                                // Не вызываем UpdateJobAsync здесь, т.к. эти поля не маппятся

                                // Настраиваем конвертацию
                                var conversion = FFmpeg.Conversions.New()
                                    .AddStream(mediaInfo.AudioStreams.First()) // Берем первый аудиопоток
                                    .SetOutputFormat("mp3")
                                    .SetAudioBitrate(128000) // 128 kbps
                                    .SetOutput(mp3Path);
                                    
                                await conversionLogger.LogSystemInfoAsync($"Задача {jobId}: Запуск FFmpeg: {conversion.Build()}");
                                logger.LogInformation("Задача {JobId}: запуск конвертации FFmpeg...", jobId);

                                // Обработка прогресса с throttling
                                DateTime lastLogTime = DateTime.MinValue;
                                DateTime lastHeartbeatTime = DateTime.UtcNow;
                                double lastLoggedPercent = -1;
                                conversion.OnProgress += async (sender, args) => {
                                    var now = DateTime.UtcNow;
                                    var percentDiff = Math.Abs(args.Percent - lastLoggedPercent);
                                    var timeDiff = (now - lastLogTime).TotalSeconds;
                                    var heartbeatDiff = (now - lastHeartbeatTime).TotalMinutes;
                                    
                                    // Логируем только если прошло 10 секунд ИЛИ прогресс изменился на 5%+
                                    if (timeDiff >= 10 || percentDiff >= 5 || args.Percent >= 99)
                                    {
                                        await conversionLogger.LogConversionProgressAsync(jobId, args.Percent, args.TotalLength.TotalSeconds - args.Duration.TotalSeconds);
                                        lastLogTime = now;
                                        lastLoggedPercent = args.Percent;
                                        logger.LogTrace("Задача {JobId}: прогресс конвертации {Percent:F1}%", jobId, args.Percent);
                                    }
                                    
                                    // Heartbeat: обновляем LastAttemptAt каждую минуту для предотвращения ложного восстановления
                                    if (heartbeatDiff >= 1)
                                    {
                                        try
                                        {
                                            var currentJob = await jobRepository.GetJobByIdAsync(jobId);
                                            if (currentJob != null && currentJob.Status == ConversionStatus.Converting)
                                            {
                                                currentJob.LastAttemptAt = DateTime.UtcNow;
                                                await jobRepository.UpdateJobAsync(currentJob);
                                                logger.LogTrace("Задача {JobId}: heartbeat - обновлен LastAttemptAt", jobId);
                                            }
                                            lastHeartbeatTime = now;
                                        }
                                        catch (Exception ex)
                                        {
                                            logger.LogWarning(ex, "Задача {JobId}: ошибка heartbeat при обновлении LastAttemptAt", jobId);
                                        }
                                    }
                                };

                                // Проверяем загрузку CPU перед запуском конвертации
                                await _cpuThrottleService.WaitIfNeededAsync(stoppingToken);
                                
                                // Запускаем конвертацию с тайм-аутом
                                using var timeoutCts = new CancellationTokenSource();
                                using var combinedCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, timeoutCts.Token);
                                
                                // Устанавливаем тайм-аут 5 минут для конвертации
                                timeoutCts.CancelAfter(TimeSpan.FromMinutes(5));
                                
                                IConversionResult result = await conversion.Start(combinedCts.Token);
                                
                                logger.LogInformation("Задача {JobId}: конвертация FFmpeg завершена.", jobId);
                                await conversionLogger.LogSystemInfoAsync($"Конвертация завершена для задания {jobId}");

                                if (!File.Exists(mp3Path))
                                {
                                    throw new InvalidOperationException($"Конвертация завершена, но MP3 файл не найден по пути: {mp3Path}");
                                }

                                var fileInfo = new FileInfo(mp3Path);
                                var mp3FileSize = fileInfo.Length;
                                
                                await conversionLogger.LogConversionCompletedAsync(jobId, mp3FileSize, mediaInfo.Duration.TotalSeconds, mp3Path);
                                logger.LogInformation("Задача {JobId}: конвертация успешно завершена. MP3 файл: {Mp3Path}, Размер: {FileSize} байт", jobId, mp3Path, mp3FileSize);

                                // Останавливаем таймер для метрик (успешная конвертация)
                                _metricsCollector.StopTimer("conversion_video_to_mp3", jobId, isSuccess: true);
                                
                                logger.LogInformation("Задача {JobId}: успешная FFmpeg конвертация", jobId);
                            });

                            // Помещаем задачу в очередь анализа аудио
                            bool audioAnalysisQueueSuccess = _channels.AudioAnalysisChannel.Writer.TryWrite((jobId, mp3Path, videoPath, videoHash));
                            if (audioAnalysisQueueSuccess)
                            {
                                logger.LogInformation("Задача {JobId}: передана в очередь анализа аудио (MP3: {Mp3Path}, Видео: {VideoPath})", jobId, mp3Path, videoPath);
                                await conversionLogger.LogSystemInfoAsync($"Задание {jobId} добавлено в очередь на анализ аудио");
                            }
                            else
                            {
                                logger.LogWarning("Задача {JobId}: очередь анализа аудио переполнена, файлы будут очищены", jobId);
                                await conversionLogger.LogErrorAsync(jobId, "Очередь анализа аудио переполнена", null, ConversionStatus.Failed);
                                await DbJobManager.UpdateJobStatusAsync(jobRepository, jobId, ConversionStatus.Failed, errorMessage: "Очередь анализа аудио переполнена");
                                // Очищаем временные файлы, так как они не будут обработаны
                                CleanupFile(tempFileManager, videoPath, logger, jobId);
                                CleanupFile(tempFileManager, mp3Path, logger, jobId);
                            }

                            // НЕ удаляем файлы videoPath и mp3Path здесь, они нужны для извлечения кадров и загрузки

                        }
                        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                        {
                            logger.LogInformation("Обработка задачи {JobId} (конвертация) отменена.", jobId);
                             // Очищаем временные файлы
                            CleanupFile(tempFileManager, videoPath, logger, jobId);
                            CleanupFile(tempFileManager, mp3Path, logger, jobId);
                        }
                        catch (OperationCanceledException)
                        {
                            // Тайм-аут конвертации
                            logger.LogError("Задача {JobId}: Превышен тайм-аут конвертации (5 минут).", jobId);
                            
                            // Останавливаем таймер для метрик (тайм-аут конвертации)
                            _metricsCollector.StopTimer("conversion_video_to_mp3", jobId, isSuccess: false);
                            
                            await conversionLogger.LogErrorAsync(jobId, "Превышен тайм-аут конвертации (5 минут)", null, ConversionStatus.Failed);
                            await DbJobManager.UpdateJobStatusAsync(jobRepository, jobId, ConversionStatus.Failed, errorMessage: "Тайм-аут конвертации");
                            // Очищаем временные файлы
                            CleanupFile(tempFileManager, videoPath, logger, jobId);
                            CleanupFile(tempFileManager, mp3Path, logger, jobId);
                        }
                        catch (Exception ex)
                        {
                            logger.LogError(ex, "Задача {JobId}: Ошибка на этапе конвертации.", jobId);
                            
                            // Останавливаем таймер для метрик (неуспешная конвертация)
                            _metricsCollector.StopTimer("conversion_video_to_mp3", jobId, isSuccess: false);
                            
                            await conversionLogger.LogErrorAsync(jobId, $"Ошибка при конвертации видео: {ex.Message}", ex.StackTrace, ConversionStatus.Failed);
                            await DbJobManager.UpdateJobStatusAsync(jobRepository, jobId, ConversionStatus.Failed, errorMessage: $"Ошибка конвертации: {ex.Message}");
                            // Очищаем временные файлы
                            CleanupFile(tempFileManager, videoPath, logger, jobId);
                            CleanupFile(tempFileManager, mp3Path, logger, jobId);
                       }
                    } // Конец using scope
                }
                catch (OperationCanceledException)
                {
                     _logger.LogInformation("ConversionWorker остановлен из-за токена отмены.");
                    break; // Выход из цикла while
               }
                catch (Exception ex)
                {
                    _logger.LogCritical(ex, "Критическая ошибка в ConversionBackgroundService WorkerLoop.");
                     await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken); 
                }
            }
        }
        
        private void CleanupFile(ITempFileManager tempFileManager, string path, ILogger logger, string jobId)
        {
             if (!string.IsNullOrEmpty(path) && File.Exists(path))
            {
                try
                {
                    tempFileManager.DeleteTempFile(path);
                    logger.LogInformation("Задача {JobId}: Временный файл {Path} удален (этап конвертации).", jobId, path);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Задача {JobId}: Ошибка при удалении временного файла: {Path} (этап конвертации)", jobId, path);
                    // Логируем также через основной логгер задачи
                    using var cleanupScope = _serviceProvider.CreateScope();
                    var conversionLogger = cleanupScope.ServiceProvider.GetRequiredService<IConversionLogger>();
                     conversionLogger.LogWarningAsync(jobId, $"Ошибка при удалении временного файла после конвертации: {path}", ex.Message).GetAwaiter().GetResult();
                }
            }
        }

        public override Task StopAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("ConversionBackgroundService останавливается.");
            // Здесь можно было бы попытаться дождаться завершения текущих FFmpeg процессов, но это сложно.
            // Полагаемся на CancellationToken в conversion.Start().
            return base.StopAsync(cancellationToken);
        }
    }
} 