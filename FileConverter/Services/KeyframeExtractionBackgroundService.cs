using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using FileConverter.Data;
using FileConverter.Models;
using FileConverter.Services.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using Xabe.FFmpeg;

namespace FileConverter.Services
{
    /// <summary>
    /// Фоновый сервис для извлечения ключевых кадров из видео из очереди KeyframeExtractionChannel.
    /// </summary>
    public class KeyframeExtractionBackgroundService : BackgroundService
    {
        private readonly ILogger<KeyframeExtractionBackgroundService> _logger;
        private readonly IServiceProvider _serviceProvider;
        private readonly ProcessingChannels _channels;
        private readonly int _maxConcurrentExtractions;
        private readonly int _keyframeCount;
        private readonly int _keyframeQuality;

        public KeyframeExtractionBackgroundService(
            ILogger<KeyframeExtractionBackgroundService> logger,
            IServiceProvider serviceProvider,
            ProcessingChannels channels,
            IConfiguration configuration)
        {
            _logger = logger;
            _serviceProvider = serviceProvider;
            _channels = channels;
            _maxConcurrentExtractions = configuration.GetValue<int>("Performance:MaxConcurrentKeyframeExtractions", Math.Max(1, Environment.ProcessorCount - 1));
            _keyframeCount = configuration.GetValue<int>("KeyframeExtraction:FrameCount", 10);
            _keyframeQuality = configuration.GetValue<int>("KeyframeExtraction:Quality", 2); // 1-31, где 1 - лучшее качество
            _logger.LogInformation("KeyframeExtractionBackgroundService инициализирован с {MaxConcurrentExtractions} параллельными извлечениями, {FrameCount} кадров, качество {Quality}.", 
                _maxConcurrentExtractions, _keyframeCount, _keyframeQuality);
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("KeyframeExtractionBackgroundService запущен.");
            
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

            var tasks = new Task[_maxConcurrentExtractions];
            for (int i = 0; i < _maxConcurrentExtractions; i++)
            {
                tasks[i] = Task.Run(() => WorkerLoop(stoppingToken), stoppingToken);
            }

            await Task.WhenAll(tasks);

            _logger.LogInformation("KeyframeExtractionBackgroundService остановлен.");
        }

        private async Task WorkerLoop(CancellationToken stoppingToken)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                string jobId = string.Empty;
                string videoPath = string.Empty;
                string mp3Path = string.Empty;
                string videoHash = string.Empty;
                List<string> keyframePaths = new List<string>();

                try
                {
                    var item = await _channels.KeyframeExtractionChannel.Reader.ReadAsync(stoppingToken);
                    jobId = item.JobId;
                    videoPath = item.VideoPath;
                    mp3Path = item.Mp3Path;
                    videoHash = item.VideoHash;

                    _logger.LogInformation("KeyframeExtractionWorker получил задачу {JobId} (Видео: {VideoPath})", jobId, videoPath);

                    using (var scope = _serviceProvider.CreateScope())
                    {
                        var jobRepository = scope.ServiceProvider.GetRequiredService<IJobRepository>();
                        var conversionLogger = scope.ServiceProvider.GetRequiredService<IConversionLogger>();
                        var tempFileManager = scope.ServiceProvider.GetRequiredService<ITempFileManager>();
                        var storageService = scope.ServiceProvider.GetRequiredService<IS3StorageService>();
                        var logger = scope.ServiceProvider.GetRequiredService<ILogger<KeyframeExtractionBackgroundService>>();
                        
                        DateTime queueStart = DateTime.UtcNow;
                        long queueTimeMs = 0;

                        try
                        {
                            var job = await jobRepository.GetJobByIdAsync(jobId);
                            if (job == null)
                            {
                                logger.LogWarning("Задача {JobId} не найдена в репозитории после извлечения из очереди извлечения кадров.", jobId);
                                await conversionLogger.LogErrorAsync(jobId, $"Задача {jobId} не найдена в БД.");
                                // Очищаем временные файлы
                                CleanupFiles(tempFileManager, keyframePaths, logger, jobId);
                                continue; 
                            }
                            
                            // Рассчитываем время ожидания в очереди
                            if (job.LastAttemptAt.HasValue)
                            {
                                queueTimeMs = (long)(DateTime.UtcNow - job.LastAttemptAt.Value).TotalMilliseconds;
                            }

                            await conversionLogger.LogSystemInfoAsync($"Задача {jobId}: начало извлечения ключевых кадров. Время в очереди: {queueTimeMs} мс");

                            // Обновляем статус (можно добавить новый статус KeyframeExtracting, но пока используем Converting)
                            await DbJobManager.UpdateJobStatusAsync(jobRepository, jobId, ConversionStatus.Converting);

                            // Получаем информацию о видеофайле
                            logger.LogDebug("Задача {JobId}: получение информации о видеофайле {VideoPath}", jobId, videoPath);
                            IMediaInfo mediaInfo = await FFmpeg.GetMediaInfo(videoPath, stoppingToken);
                            logger.LogDebug("Задача {JobId}: информация получена, длительность {Duration}", jobId, mediaInfo.Duration);

                            if (mediaInfo.VideoStreams?.Any() != true)
                            {
                                throw new InvalidOperationException("Видеопоток не найден в видеофайле.");
                            }

                            var videoStream = mediaInfo.VideoStreams.First();
                            var duration = mediaInfo.Duration.TotalSeconds;
                            
                            // Создаем временную папку для кадров
                            var keyframesDir = tempFileManager.CreateTempDirectory();
                            logger.LogInformation("Задача {JobId}: создана временная папка для кадров {KeyframesDir}", jobId, keyframesDir);

                            // Извлекаем ключевые кадры
                            keyframePaths = await ExtractKeyframes(videoPath, keyframesDir, duration, jobId, conversionLogger, logger, stoppingToken);
                            
                            logger.LogInformation("Задача {JobId}: извлечено {FrameCount} ключевых кадров", jobId, keyframePaths.Count);

                            // Передаем задачу в очередь загрузки с путями к файлам кадров
                            await _channels.UploadChannel.Writer.WriteAsync((jobId, mp3Path, videoPath, videoHash, keyframePaths), stoppingToken);
                            logger.LogInformation("Задача {JobId}: передана в очередь загрузки с {FrameCount} ключевыми кадрами", jobId, keyframePaths.Count);
                            await conversionLogger.LogSystemInfoAsync($"Задание {jobId} добавлено в очередь на загрузку с {keyframePaths.Count} ключевыми кадрами");

                            // НЕ удаляем файлы videoPath, mp3Path и keyframePaths здесь, они нужны для загрузки
                        }
                        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                        {
                            logger.LogInformation("Обработка задачи {JobId} (извлечение кадров) отменена.", jobId);
                            // Очищаем временные файлы
                            CleanupFiles(tempFileManager, keyframePaths, logger, jobId);
                            // Также удаляем mp3 и video, так как процесс прерван
                            CleanupFile(tempFileManager, videoPath, logger, jobId);
                            CleanupFile(tempFileManager, mp3Path, logger, jobId);
                        }
                        catch (Exception ex)
                        {
                            logger.LogError(ex, "Задача {JobId}: Ошибка на этапе извлечения ключевых кадров.", jobId);
                            await conversionLogger.LogErrorAsync(jobId, $"Ошибка при извлечении ключевых кадров: {ex.Message}", ex.StackTrace, ConversionStatus.Failed);
                            await DbJobManager.UpdateJobStatusAsync(jobRepository, jobId, ConversionStatus.Failed, errorMessage: $"Ошибка извлечения кадров: {ex.Message}");
                            // Очищаем временные файлы
                            CleanupFiles(tempFileManager, keyframePaths, logger, jobId);
                            // Также удаляем mp3 и video, так как процесс прерван
                            CleanupFile(tempFileManager, videoPath, logger, jobId);
                            CleanupFile(tempFileManager, mp3Path, logger, jobId);
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    _logger.LogInformation("KeyframeExtractionWorker остановлен из-за токена отмены.");
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogCritical(ex, "Критическая ошибка в KeyframeExtractionBackgroundService WorkerLoop.");
                    await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
                }
            }
        }

        private async Task<List<string>> ExtractKeyframes(string videoPath, string outputDir, double durationSeconds, 
            string jobId, IConversionLogger conversionLogger, ILogger logger, CancellationToken stoppingToken)
        {
            var keyframePaths = new List<string>();
            
            // Вычисляем интервалы для извлечения кадров
            var interval = durationSeconds / (_keyframeCount + 1); // +1 чтобы не брать самый последний кадр
            
            for (int i = 1; i <= _keyframeCount; i++)
            {
                var timePosition = TimeSpan.FromSeconds(interval * i);
                // Используем уникальное имя файла с jobId для потокобезопасности
                var outputPath = Path.Combine(outputDir, $"{jobId}_keyframe_{i:D3}.jpg");
                
                // Создаем команду FFmpeg для извлечения кадра
                var conversion = FFmpeg.Conversions.New()
                    .AddParameter($"-i \"{videoPath}\"")
                    .AddParameter($"-ss {timePosition:hh\\:mm\\:ss\\.fff}")
                    .AddParameter("-vframes 1")
                    .AddParameter($"-q:v {_keyframeQuality}")
                    .SetOutput(outputPath);

                await conversionLogger.LogSystemInfoAsync($"Задача {jobId}: извлечение кадра {i}/{_keyframeCount} в позиции {timePosition:hh\\:mm\\:ss}");
                
                try
                {
                    await conversion.Start(stoppingToken);
                    
                    if (File.Exists(outputPath))
                    {
                        keyframePaths.Add(outputPath);
                        logger.LogDebug("Задача {JobId}: кадр {FrameNumber} успешно извлечен: {OutputPath}", 
                            jobId, i, outputPath);
                    }
                    else
                    {
                        logger.LogWarning("Задача {JobId}: не удалось извлечь кадр {FrameNumber} в позиции {TimePosition}", 
                            jobId, i, timePosition);
                    }
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Задача {JobId}: ошибка при извлечении кадра {FrameNumber} в позиции {TimePosition}", 
                        jobId, i, timePosition);
                    // Продолжаем с следующим кадром, не прерываем весь процесс
                }
            }
            
            return keyframePaths;
        }

        private void CleanupFiles(ITempFileManager tempFileManager, List<string> filePaths, ILogger logger, string jobId)
        {
            foreach (var filePath in filePaths)
            {
                if (!string.IsNullOrEmpty(filePath) && File.Exists(filePath))
                {
                    try
                    {
                        tempFileManager.DeleteTempFile(filePath);
                        logger.LogDebug("Задача {JobId}: временный файл кадра {Path} удален.", jobId, filePath);
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "Задача {JobId}: ошибка при удалении временного файла кадра: {Path}", jobId, filePath);
                    }
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
                    logger.LogInformation("Задача {JobId}: Временный файл {Path} удален (этап извлечения кадров).", jobId, path);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Задача {JobId}: Ошибка при удалении временного файла: {Path} (этап извлечения кадров)", jobId, path);
                }
            }
        }

        public override Task StopAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("KeyframeExtractionBackgroundService получил сигнал остановки.");
            // Здесь можно было бы попытаться дождаться завершения текущих FFmpeg процессов, но это сложно.
            // Полагаемся на CancellationToken для корректной остановки.
            return base.StopAsync(cancellationToken);
        }
    }
} 