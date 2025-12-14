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
using System.Security.Cryptography;
using System.Text;

namespace FileConverter.Services
{
    /// <summary>
    /// Фоновый сервис для обработки YouTube видео из очереди YoutubeDownloadChannel
    /// </summary>
    public class YoutubeBackgroundService : BackgroundService
    {
        private readonly ILogger<YoutubeBackgroundService> _logger;
        private readonly IServiceProvider _serviceProvider;
        private readonly ProcessingChannels _channels;
        private readonly CpuThrottleService _cpuThrottleService;
        private readonly int _maxConcurrentDownloads;

        public YoutubeBackgroundService(
            ILogger<YoutubeBackgroundService> logger,
            IServiceProvider serviceProvider,
            ProcessingChannels channels,
            CpuThrottleService cpuThrottleService,
            IConfiguration configuration)
        {
            _logger = logger;
            _serviceProvider = serviceProvider;
            _channels = channels;
            _cpuThrottleService = cpuThrottleService;
            _maxConcurrentDownloads = configuration.GetValue<int>("Performance:MaxConcurrentYoutubeDownloads", 3);
            // Логирование инициализации убрано для уменьшения количества логов
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {

            var tasks = new Task[_maxConcurrentDownloads];
            for (int i = 0; i < _maxConcurrentDownloads; i++)
            {
                tasks[i] = Task.Run(() => WorkerLoop(stoppingToken), stoppingToken);
            }

            await Task.WhenAll(tasks);
        }

        private async Task WorkerLoop(CancellationToken stoppingToken)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                string jobId = string.Empty;
                string videoUrl = string.Empty;
                string mp3Path = string.Empty;

                try
                {
                    // Ожидаем задачу из YouTube канала
                    var item = await _channels.YoutubeDownloadChannel.Reader.ReadAsync(stoppingToken);
                    jobId = item.JobId;
                    videoUrl = item.VideoUrl;

                    _logger.LogInformation("YoutubeWorker получил задачу {JobId} для URL: {VideoUrl}", jobId, videoUrl);

                    using var scope = _serviceProvider.CreateScope();
                    var jobRepository = scope.ServiceProvider.GetRequiredService<IJobRepository>();
                    var mediaItemRepository = scope.ServiceProvider.GetRequiredService<IMediaItemRepository>();
                    var conversionLogger = scope.ServiceProvider.GetRequiredService<IConversionLogger>();
                    var youtubeService = scope.ServiceProvider.GetRequiredService<IYoutubeDownloadService>();
                    var logger = scope.ServiceProvider.GetRequiredService<ILogger<YoutubeBackgroundService>>();

                    try
                    {
                        var job = await jobRepository.GetJobByIdAsync(jobId);
                        if (job == null)
                        {
                            logger.LogWarning("Задача {JobId} не найдена в репозитории после извлечения из YouTube очереди.", jobId);
                            await conversionLogger.LogErrorAsync(jobId, $"Задача {jobId} не найдена в БД.");
                            continue;
                        }

                        long queueTimeMs = (long)(DateTime.UtcNow - job.CreatedAt).TotalMilliseconds;
                        await conversionLogger.LogDownloadStartedAsync(jobId, videoUrl, queueTimeMs);

                        // Обновляем статус на Downloading
                        await DbJobManager.UpdateJobStatusAsync(jobRepository, jobId, ConversionStatus.Downloading);
                        await conversionLogger.LogStatusChangedAsync(jobId, ConversionStatus.Downloading);

                        // Вычисляем хеш для видео URL (для кэширования)
                        string videoHash;
                        using (var sha = SHA256.Create())
                        {
                            var hashBytes = sha.ComputeHash(Encoding.UTF8.GetBytes(videoUrl));
                            videoHash = Convert.ToBase64String(hashBytes);
                        }

                        // Проверяем наличие готового MP3 в репозитории по хешу видео
                        var existingItem = await mediaItemRepository.FindByVideoHashAsync(videoHash);
                        // Если есть, то обновляем задачи
                        if (existingItem != null && !string.IsNullOrEmpty(existingItem.AudioUrl))
                        {
                            await conversionLogger.LogCacheHitAsync(jobId, existingItem.AudioUrl, videoHash);

                            job = await jobRepository.GetJobByIdAsync(jobId);
                            if (job != null)
                            {
                                job.Status = ConversionStatus.Completed;
                                job.Mp3Url = existingItem.AudioUrl;
                                job.VideoUrl = videoUrl;
                                job.NewVideoUrl = existingItem.VideoUrl;
                                job.VideoHash = videoHash;
                                job.LastAttemptAt = DateTime.UtcNow;
                                
                                // Сохраняем ключевые кадры если они есть
                                if (existingItem.Keyframes != null && existingItem.Keyframes.Count > 0)
                                {
                                    job.Keyframes = existingItem.Keyframes;
                                }
                                
                                await jobRepository.UpdateJobAsync(job);
                                logger.LogDebug("Задача {JobId}: информация о файле обновлена в БД.", jobId);
                            }
                            else
                            {
                                logger.LogDebug("Задача {JobId}: не найдена в БД.", jobId);
                            }

                            var totalTimeMs = (long)(DateTime.UtcNow - job.CreatedAt).TotalMilliseconds; // Время от создания задачи
                            await conversionLogger.LogJobCompletedAsync(jobId, existingItem.AudioUrl, totalTimeMs);
                            continue;
                        }

                        // Проверяем загрузку CPU перед началом скачивания и конвертации
                        await _cpuThrottleService.WaitIfNeededAsync(stoppingToken);
                        
                        // Скачиваем и конвертируем YouTube видео в MP3
                        mp3Path = await youtubeService.DownloadAndConvertToMp3Async(videoUrl, jobId, stoppingToken);

                        var fileInfo = new FileInfo(mp3Path);
                        var mp3FileSize = fileInfo.Length;

                        logger.LogInformation("Задача {JobId}: YouTube видео успешно скачано и сконвертировано в MP3: {Mp3Path}, размер: {FileSize} байт", 
                            jobId, mp3Path, mp3FileSize);

                        await conversionLogger.LogDownloadCompletedAsync(jobId, mp3FileSize, mp3Path);

                        bool uploadQueueSuccess = _channels.UploadChannel.Writer.TryWrite((jobId, mp3Path, videoUrl, videoHash, new List<KeyframeInfo>()));
                        if (!uploadQueueSuccess)
                        {
                            logger.LogWarning("Задача {JobId}: очередь загрузки переполнена", jobId);
                            await conversionLogger.LogErrorAsync(jobId, "Очередь загрузки переполнена", null, ConversionStatus.Failed);
                            await DbJobManager.UpdateJobStatusAsync(jobRepository, jobId, ConversionStatus.Failed, errorMessage: "Очередь загрузки переполнена");
                        }
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(ex, "Задача {JobId}: Ошибка при обработке YouTube видео", jobId);
                        await conversionLogger.LogErrorAsync(jobId, $"Ошибка обработки YouTube видео: {ex.Message}", ex.StackTrace);
                        
                        try
                        {
                            await DbJobManager.UpdateJobStatusAsync(jobRepository, jobId, ConversionStatus.Failed, 
                                errorMessage: $"Ошибка обработки YouTube видео: {ex.Message}");
                        }
                        catch (Exception updateEx)
                        {
                            logger.LogError(updateEx, "Задача {JobId}: Не удалось обновить статус на Failed", jobId);
                        }
                    }
                    finally
                    {
                        // Очищаем временный MP3 файл (ИСПРАВЛЕНО: добавлено фактическое удаление)
                        var tempFileManager = scope.ServiceProvider.GetRequiredService<ITempFileManager>();
                        CleanupFile(tempFileManager, mp3Path, logger, jobId);
                    }
                }
                catch (OperationCanceledException)
                {
                    _logger.LogInformation("YoutubeWorker остановлен из-за токена отмены.");
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogCritical(ex, "Критическая ошибка в YoutubeBackgroundService WorkerLoop.");
                    await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
                }
            }
        }

        /// <summary>
        /// Стандартизированный метод очистки временных файлов (унификация с другими сервисами)
        /// </summary>
        private void CleanupFile(ITempFileManager tempFileManager, string path, ILogger logger, string jobId)
        {
            if (!string.IsNullOrEmpty(path) && File.Exists(path))
            {
                try
                {
                    tempFileManager.DeleteTempFile(path);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Задача {JobId}: Ошибка при удалении временного файла: {Path} (этап YouTube)", jobId, path);
                    // Логируем также через основной логгер задачи
                    using var cleanupScope = _serviceProvider.CreateScope();
                    var conversionLogger = cleanupScope.ServiceProvider.GetRequiredService<IConversionLogger>();
                    conversionLogger.LogWarningAsync(jobId, $"Ошибка при удалении временного файла после YouTube обработки: {path}", ex.Message).GetAwaiter().GetResult();
                }
            }
        }

        public override Task StopAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("YoutubeBackgroundService останавливается.");
            return base.StopAsync(cancellationToken);
        }
    }
} 