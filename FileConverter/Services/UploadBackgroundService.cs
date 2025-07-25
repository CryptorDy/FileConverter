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
using System.Collections.Generic;
using System.Linq;

namespace FileConverter.Services
{
    /// <summary>
    /// Фоновый сервис для загрузки готовых MP3 и видео в S3 из очереди UploadChannel.
    /// </summary>
    public class UploadBackgroundService : BackgroundService
    {
        private readonly ILogger<UploadBackgroundService> _logger;
        private readonly IServiceProvider _serviceProvider;
        private readonly ProcessingChannels _channels;
        private readonly int _maxConcurrentUploads;

        public UploadBackgroundService(
            ILogger<UploadBackgroundService> logger,
            IServiceProvider serviceProvider,
            ProcessingChannels channels,
            IConfiguration configuration)
        {
            _logger = logger;
            _serviceProvider = serviceProvider;
            _channels = channels;
             _maxConcurrentUploads = configuration.GetValue<int>("Performance:MaxConcurrentUploads", 5); 
             _logger.LogInformation("UploadBackgroundService инициализирован с {MaxConcurrentUploads} параллельными загрузками.", _maxConcurrentUploads);
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("UploadBackgroundService запущен.");

            var tasks = new Task[_maxConcurrentUploads];
            for (int i = 0; i < _maxConcurrentUploads; i++)
            {
                tasks[i] = Task.Run(() => WorkerLoop(stoppingToken), stoppingToken);
            }

            await Task.WhenAll(tasks);

            _logger.LogInformation("UploadBackgroundService остановлен.");
        }

        private async Task WorkerLoop(CancellationToken stoppingToken)
        {
             while (!stoppingToken.IsCancellationRequested)
            {
                string jobId = string.Empty;
                string mp3Path = string.Empty;   // Путь к временному MP3 файлу
                string videoPath = string.Empty; // Путь к временному видео файлу
                string videoHash = string.Empty;
                List<KeyframeInfo> keyframeInfos = new List<KeyframeInfo>();

                try
                {
                    var item = await _channels.UploadChannel.Reader.ReadAsync(stoppingToken);
                    jobId = item.JobId;
                    mp3Path = item.Mp3Path;
                    videoPath = item.VideoPath;
                    videoHash = item.VideoHash;
                    keyframeInfos = item.KeyframeInfos ?? new List<KeyframeInfo>();

                     _logger.LogInformation("UploadWorker получил задачу {JobId} (MP3: {Mp3Path}, Видео: {VideoPath}, Кадров: {KeyframeCount})", 
                         jobId, mp3Path, videoPath, keyframeInfos.Count);

                    using (var scope = _serviceProvider.CreateScope())
                    {
                        var jobRepository = scope.ServiceProvider.GetRequiredService<IJobRepository>();
                        var conversionLogger = scope.ServiceProvider.GetRequiredService<IConversionLogger>();
                        var storageService = scope.ServiceProvider.GetRequiredService<IS3StorageService>();
                        var mediaItemRepository = scope.ServiceProvider.GetRequiredService<IMediaItemRepository>();
                        var tempFileManager = scope.ServiceProvider.GetRequiredService<ITempFileManager>();
                        var logger = scope.ServiceProvider.GetRequiredService<ILogger<UploadBackgroundService>>();

                        DateTime queueStart = DateTime.UtcNow; 
                        long queueTimeMs = 0;
                        
                        try
                        {
                            var job = await jobRepository.GetJobByIdAsync(jobId);
                            if (job == null)
                            {
                                logger.LogWarning("Задача {JobId} не найдена в репозитории после извлечения из очереди загрузки.", jobId);
                                await conversionLogger.LogErrorAsync(jobId, $"Задача {jobId} не найдена в БД.");
                                // Очищаем временные файлы, если они остались
                                CleanupFile(tempFileManager, videoPath, logger, jobId);
                                CleanupFile(tempFileManager, mp3Path, logger, jobId);
                                CleanupFiles(tempFileManager, keyframeInfos?.Select(k => Path.GetFileName(k.Url)).ToList() ?? new List<string>(), logger, jobId);
                                continue; 
                            }
                            
                            // Рассчитываем время ожидания в очереди загрузки
                            if (job.LastAttemptAt.HasValue)
                            {
                                queueTimeMs = (long)(DateTime.UtcNow - job.LastAttemptAt.Value).TotalMilliseconds;
                            }

                            var fileInfo = new FileInfo(mp3Path);
                            var mp3FileSize = fileInfo.Length;

                            await conversionLogger.LogUploadStartedAsync(jobId, queueTimeMs, mp3FileSize);

                            // Обновляем статус на Uploading
                            await DbJobManager.UpdateJobStatusAsync(jobRepository, jobId, ConversionStatus.Uploading);
                            await conversionLogger.LogStatusChangedAsync(jobId, ConversionStatus.Uploading);
                            
                            logger.LogInformation("Задача {JobId}: начало загрузки файлов в S3...", jobId);

                            // Параллельно загружаем видео, MP3 и ключевые кадры в S3
                            var videoUploadTask = storageService.UploadFileAsync(videoPath, "video/mp4");
                            var mp3UploadTask = storageService.UploadFileAsync(mp3Path, "audio/mpeg");
                            var keyframesUploadTasks = keyframeInfos.Select(k => storageService.UploadFileAsync(k.Url, "image/jpeg")).ToList();

                            // Ожидаем завершения всех задач загрузки
                            await Task.WhenAll(videoUploadTask, mp3UploadTask); // Основные файлы
                            var keyframeUrls = (await Task.WhenAll(keyframesUploadTasks)).ToList(); // Кадры
                            
                            var videoUrl = await videoUploadTask; // URL видео в S3
                            var mp3Url = await mp3UploadTask;   // URL MP3 в S3
                            
                            // Обновляем URL-ы в keyframeInfos после загрузки
                            for (int i = 0; i < keyframeInfos.Count && i < keyframeUrls.Count; i++)
                            {
                                keyframeInfos[i].Url = keyframeUrls[i];
                            }
                            
                            logger.LogInformation("Задача {JobId}: файлы загружены. Видео URL: {VideoUrl}, MP3 URL: {Mp3Url}, Кадров: {KeyframeCount}", 
                                jobId, videoUrl, mp3Url, keyframeInfos.Count);
                            await conversionLogger.LogUploadCompletedAsync(jobId, mp3Url);
                            await conversionLogger.LogSystemInfoAsync($"Файлы загружены для задания {jobId}. URL видео: {videoUrl}, URL MP3: {mp3Url}, ключевых кадров: {keyframeInfos.Count}");

                            // Сохраняем информацию о файлах в репозиторий MediaItems
                            var mediaItem = new MediaStorageItem
                            {
                                VideoHash = videoHash,
                                VideoUrl = videoUrl,
                                AudioUrl = mp3Url,
                                Keyframes = keyframeInfos, // Добавляем информацию с таймкодами
                                FileSizeBytes = job.FileSizeBytes ?? 0, // Берем размер из задачи
                                DurationSeconds = job.DurationSeconds // Сохраняем длительность видео
                            };
                            
                            var savedItem = await mediaItemRepository.SaveItemAsync(mediaItem);
                            logger.LogInformation("Задача {JobId}: медиа элемент сохранен в репозиторий MediaItems с ID: {MediaItemId}", jobId, savedItem.Id);
                            await conversionLogger.LogSystemInfoAsync($"Медиа элемент сохранен в хранилище C3 с ID: {savedItem.Id}");

                            // Обновляем статус задачи на Completed
                            await DbJobManager.UpdateJobStatusAsync(jobRepository, jobId, ConversionStatus.Completed, 
                                mp3Url: mp3Url, 
                                newVideoUrl: videoUrl);
                            await conversionLogger.LogStatusChangedAsync(jobId, ConversionStatus.Completed);

                            // Вычисляем общее время выполнения задачи
                            var totalTimeMs = (long)(DateTime.UtcNow - job.CreatedAt).TotalMilliseconds;
                            
                            await conversionLogger.LogJobCompletedAsync(jobId, mp3Url, totalTimeMs);
                            logger.LogInformation("Задача {JobId}: успешно завершена за {TotalTimeMs} мс.", jobId, totalTimeMs);
                             await conversionLogger.LogSystemInfoAsync($"Задание {jobId} успешно завершено");
                        }
                        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                        {
                            logger.LogInformation("Обработка задачи {JobId} (загрузка) отменена.", jobId);
                             // Очищаем временные файлы
                            CleanupFile(tempFileManager, videoPath, logger, jobId);
                            CleanupFile(tempFileManager, mp3Path, logger, jobId);
                            CleanupFiles(tempFileManager, keyframeInfos?.Select(k => Path.GetFileName(k.Url)).ToList() ?? new List<string>(), logger, jobId);
                        }
                        catch (Exception ex)
                        {
                            logger.LogError(ex, "Задача {JobId}: Ошибка на этапе загрузки.", jobId);
                            await conversionLogger.LogErrorAsync(jobId, $"Ошибка при загрузке MP3: {ex.Message}", ex.StackTrace, ConversionStatus.Failed);
                            await DbJobManager.UpdateJobStatusAsync(jobRepository, jobId, ConversionStatus.Failed, errorMessage: $"Ошибка загрузки: {ex.Message}");
                       }
                        finally
                        {
                             // Удаляем временные файлы после завершения (успешного или неуспешного) этапа загрузки
                            CleanupFile(tempFileManager, videoPath, logger, jobId);
                            CleanupFile(tempFileManager, mp3Path, logger, jobId);
                            CleanupFiles(tempFileManager, keyframeInfos?.Select(k => Path.GetFileName(k.Url)).ToList() ?? new List<string>(), logger, jobId);
                        }
                    } // Конец using scope
                }
                catch (OperationCanceledException)
                {
                    _logger.LogInformation("UploadWorker остановлен из-за токена отмены.");
                    break; // Выход из цикла while
                }
                catch (Exception ex)
                {
                    _logger.LogCritical(ex, "Критическая ошибка в UploadBackgroundService WorkerLoop.");
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
                    logger.LogInformation("Задача {JobId}: Временный файл {Path} удален (этап загрузки).", jobId, path);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Задача {JobId}: Ошибка при удалении временного файла: {Path} (этап загрузки)", jobId, path);
                    // Логируем также через основной логгер задачи
                    using var cleanupScope = _serviceProvider.CreateScope();
                    var conversionLogger = cleanupScope.ServiceProvider.GetRequiredService<IConversionLogger>();
                    conversionLogger.LogWarningAsync(jobId, $"Ошибка при удалении временного файла после загрузки: {path}", ex.Message).GetAwaiter().GetResult();
                }
            }
        }

        private void CleanupFiles(ITempFileManager tempFileManager, List<string> filePaths, ILogger logger, string jobId)
        {
            if (filePaths == null) return;
            
            foreach (var filePath in filePaths)
            {
                CleanupFile(tempFileManager, filePath, logger, jobId);
            }
        }

         public override Task StopAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("UploadBackgroundService останавливается.");
            // Дожидаемся завершения текущих загрузок? Сложно, т.к. UploadFileAsync может быть долгим.
            // Полагаемся на то, что S3 SDK обработает CancellationToken, если он там используется.
            return base.StopAsync(cancellationToken);
        }
    }
} 