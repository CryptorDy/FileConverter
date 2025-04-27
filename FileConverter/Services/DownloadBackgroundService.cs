using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using FileConverter.Data;
using FileConverter.Models;
using FileConverter.Services.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using Newtonsoft.Json;

namespace FileConverter.Services
{
    /// <summary>
    /// Фоновый сервис для скачивания видео из очереди DownloadChannel.
    /// </summary>
    public class DownloadBackgroundService : BackgroundService
    {
        private readonly ILogger<DownloadBackgroundService> _logger;
        private readonly IServiceProvider _serviceProvider;
        private readonly ProcessingChannels _channels;
        private readonly int _maxConcurrentDownloads;

        public DownloadBackgroundService(
            ILogger<DownloadBackgroundService> logger,
            IServiceProvider serviceProvider,
            ProcessingChannels channels,
            IConfiguration configuration)
        {
            _logger = logger;
            _serviceProvider = serviceProvider;
            _channels = channels;
            // Получаем максимальное количество параллельных загрузок из конфигурации
            _maxConcurrentDownloads = configuration.GetValue<int>("Performance:MaxConcurrentDownloads", 5); 
            _logger.LogInformation("DownloadBackgroundService инициализирован с {MaxConcurrentDownloads} параллельными загрузками.", _maxConcurrentDownloads);
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("DownloadBackgroundService запущен.");

            // Создаем ограниченное количество параллельных задач для загрузки
            var tasks = new Task[_maxConcurrentDownloads];
            for (int i = 0; i < _maxConcurrentDownloads; i++)
            {
                tasks[i] = Task.Run(() => WorkerLoop(stoppingToken), stoppingToken);
            }

            await Task.WhenAll(tasks);

            _logger.LogInformation("DownloadBackgroundService остановлен.");
        }

        private async Task WorkerLoop(CancellationToken stoppingToken)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                string jobId = string.Empty;
                string videoUrl = string.Empty;
                string videoPath = string.Empty; // Путь к временному файлу
                
                try
                {
                    // Ожидаем задачу из канала
                    var item = await _channels.DownloadChannel.Reader.ReadAsync(stoppingToken);
                    jobId = item.JobId;
                    videoUrl = item.VideoUrl;
                    
                    _logger.LogInformation("DownloadWorker получил задачу {JobId} для URL: {VideoUrl}", jobId, videoUrl);

                    // Создаем новый scope для обработки этой задачи
                    // Это гарантирует, что все Scoped зависимости (DbContext, HttpClient и т.д.) будут корректно созданы и уничтожены
                    using (var scope = _serviceProvider.CreateScope())
                    {
                        var jobRepository = scope.ServiceProvider.GetRequiredService<IJobRepository>();
                        var conversionLogger = scope.ServiceProvider.GetRequiredService<IConversionLogger>();
                        var storageService = scope.ServiceProvider.GetRequiredService<IS3StorageService>();
                        var tempFileManager = scope.ServiceProvider.GetRequiredService<ITempFileManager>();
                        // Используем IHttpClientFactory для получения именованного клиента
                        var httpClientFactory = scope.ServiceProvider.GetRequiredService<IHttpClientFactory>();
                        var instagramHttpClient = httpClientFactory.CreateClient("instagram-downloader"); 
                        var logger = scope.ServiceProvider.GetRequiredService<ILogger<DownloadBackgroundService>>(); // Логгер для контекста задачи
                        
                        DateTime queueStart = DateTime.UtcNow; // Время начала обработки из очереди
                        long queueTimeMs = 0;
                        
                        try
                        {
                            var job = await jobRepository.GetJobByIdAsync(jobId);
                            if (job == null)
                            {
                                logger.LogWarning("Задача {JobId} не найдена в репозитории после извлечения из очереди.", jobId);
                                await conversionLogger.LogErrorAsync(jobId, $"Задача {jobId} не найдена в БД.");
                                continue; // Переходим к следующей итерации
                            }
                            queueTimeMs = (long)(DateTime.UtcNow - job.CreatedAt).TotalMilliseconds;
                            
                            await conversionLogger.LogDownloadStartedAsync(jobId, videoUrl, queueTimeMs);
                            
                            // Обновляем статус на Downloading
                            await DbJobManager.UpdateJobStatusAsync(jobRepository, jobId, ConversionStatus.Downloading);
                            await conversionLogger.LogStatusChangedAsync(jobId, ConversionStatus.Downloading);

                            // Скачиваем видео
                            byte[] fileData;
                            string sourceDescription;

                            if (await storageService.FileExistsAsync(videoUrl)) // Проверяем, не лежит ли файл уже в нашем S3
                            {
                                sourceDescription = "S3 хранилища";
                                fileData = await storageService.DownloadFileAsync(videoUrl);
                                await conversionLogger.LogSystemInfoAsync($"Видео для {jobId} скачано из S3: {videoUrl}");
                                await conversionLogger.LogDownloadProgressAsync(jobId, fileData.Length, fileData.Length);
                            }
                            else
                            {
                                sourceDescription = $"URL ({ (IsInstagramUrl(videoUrl) ? "instagram-downloader" : "default") })";
                                logger.LogInformation("Задача {JobId}: скачивание из {SourceDescription}: {VideoUrl}", jobId, sourceDescription, videoUrl);
                                try
                                {
                                    // Используем соответствующий HttpClient
                                    HttpClient clientToUse = IsInstagramUrl(videoUrl) ? instagramHttpClient : httpClientFactory.CreateClient("video-downloader");

                                    using var request = new HttpRequestMessage(HttpMethod.Get, videoUrl);
                                    // Используем CancellationToken для возможности отмены запроса
                                    using var response = await clientToUse.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, stoppingToken);

                                    if (response.StatusCode == System.Net.HttpStatusCode.Forbidden)
                                        throw new InvalidOperationException($"Доступ запрещен (403 Forbidden). URL может требовать авторизации или не поддерживает прямое скачивание: {videoUrl}");
                                    if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                                        throw new InvalidOperationException($"Файл не найден (404 Not Found): {videoUrl}");
                                    if (!response.IsSuccessStatusCode)
                                        throw new InvalidOperationException($"HTTP ошибка при скачивании: {(int)response.StatusCode} {response.ReasonPhrase}");

                                    fileData = await response.Content.ReadAsByteArrayAsync(stoppingToken);
                                    await conversionLogger.LogSystemInfoAsync($"Видео для {jobId} скачано по {sourceDescription}: {videoUrl}");
                                    await conversionLogger.LogDownloadProgressAsync(jobId, fileData.Length, fileData.Length);
                                }
                                catch (HttpRequestException httpEx)
                                {
                                    if (httpEx.Message.Contains("403"))
                                        throw new InvalidOperationException($"Доступ запрещен (403 Forbidden). URL требует авторизации: {videoUrl}", httpEx);
                                    else
                                        throw new InvalidOperationException($"HTTP ошибка при скачивании: {httpEx.Message}", httpEx);
                                }
                                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                                {
                                    logger.LogInformation("Операция скачивания для задачи {JobId} отменена.", jobId);
                                    throw; // Повторно выбрасываем исключение для корректной обработки отмены
                                }
                            }

                            // Создаем временный файл
                            videoPath = tempFileManager.CreateTempFile(".mp4");
                            logger.LogInformation("Задача {JobId}: создан временный файл {VideoPath}", jobId, videoPath);

                            await File.WriteAllBytesAsync(videoPath, fileData, stoppingToken);
                            logger.LogInformation("Задача {JobId}: видео сохранено во временный файл {VideoPath}", jobId, videoPath);
                            await conversionLogger.LogDownloadCompletedAsync(jobId, fileData.Length, videoPath);

                            // Вычисляем хеш видео
                            string videoHash = VideoHasher.GetHash(fileData);
                            logger.LogInformation("Задача {JobId}: хеш видео {VideoHash}", jobId, videoHash);

                            // Проверяем наличие готового MP3 в репозитории по хешу видео
                            var mediaItemRepository = scope.ServiceProvider.GetRequiredService<IMediaItemRepository>();
                            var existingItem = await mediaItemRepository.FindByVideoHashAsync(videoHash);
                            // Если есть, то обновляем задачи
                            if (existingItem != null && !string.IsNullOrEmpty(existingItem.AudioUrl))
                            {
                                logger.LogInformation("Задача {JobId}: найдена готовая конвертация (хеш {VideoHash}), MP3: {AudioUrl}", jobId, videoHash, existingItem.AudioUrl);
                                await conversionLogger.LogCacheHitAsync(jobId, existingItem.AudioUrl, videoHash);

                                job = await jobRepository.GetJobByIdAsync(jobId);
                                if (job != null)
                                {
                                    job.FileSizeBytes = fileData.Length;
                                    job.Status = ConversionStatus.Completed;
                                    job.Mp3Url = existingItem.AudioUrl;
                                    job.VideoUrl = videoUrl;
                                    job.NewVideoUrl = existingItem.VideoUrl;
                                    job.VideoHash = videoHash;
                                    job.LastAttemptAt = DateTime.UtcNow;
                                    await jobRepository.UpdateJobAsync(job);
                                    logger.LogInformation($"------ " + JsonConvert.SerializeObject(job));
                                    logger.LogDebug("Задача {JobId}: информация о файле обновлена в БД.", jobId);
                                } else
                                {
                                    logger.LogDebug("Задача {JobId}: не найдена в БД.", jobId);
                                }

                                var totalTimeMs = (long)(DateTime.UtcNow - job.CreatedAt).TotalMilliseconds; // Время от создания задачи
                                await conversionLogger.LogJobCompletedAsync(jobId, existingItem.AudioUrl, totalTimeMs);
                                // ВАЖНО: Удаляем временный видеофайл, так как он больше не нужен
                                CleanupFile(tempFileManager, videoPath, logger, jobId);
                                continue; // Переходим к следующей задаче
                            }

                            // Обновляем задачу в БД
                            try
                            {
                                job = await jobRepository.GetJobByIdAsync(jobId); // Перечитываем задачу, т.к. она могла измениться
                                if (job != null)
                                {
                                    job.FileSizeBytes = fileData.Length;
                                    job.TempVideoPath = videoPath; // Сохраняем путь к временному файлу (не в БД)
                                    job.VideoHash = videoHash;
                                    job.LastAttemptAt = DateTime.UtcNow; // Обновляем время последней активности
                                    await jobRepository.UpdateJobAsync(job);
                                    logger.LogDebug("Задача {JobId}: информация о файле обновлена в БД.", jobId);
                                }
                            }
                            catch (Exception updateEx)
                            {
                                await conversionLogger.LogErrorAsync(jobId, $"Ошибка обновления информации о файле: {updateEx.Message}", updateEx.StackTrace);
                                logger.LogError(updateEx, "Задача {JobId}: Ошибка обновления информации о файле в БД", jobId);
                            }

                            // Помещаем задачу в очередь конвертации
                            await _channels.ConversionChannel.Writer.WriteAsync((jobId, videoPath, videoHash), stoppingToken);
                            logger.LogInformation("Задача {JobId}: передана в очередь конвертации (файл {VideoPath}, хеш {VideoHash})", jobId, videoPath, videoHash);
                            await conversionLogger.LogSystemInfoAsync($"Задание {jobId} добавлено в очередь на конвертацию с хешем видео: {videoHash}");
                            
                            // НЕ удаляем videoPath здесь, он нужен для конвертации

                        }
                        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                        {
                            // Если операция была отменена во время обработки
                            logger.LogInformation("Обработка задачи {JobId} отменена.", jobId);
                            // Статус задачи не меняем, она может быть подхвачена снова или обработана JobRecoveryService
                            // Важно очистить временный файл, если он был создан
                            if (!string.IsNullOrEmpty(videoPath) && File.Exists(videoPath))
                            {
                                CleanupFile(tempFileManager, videoPath, logger, jobId);
                            }
                        }
                        catch (Exception ex)
                        {
                            // Логируем ошибку и обновляем статус задачи на Failed
                            logger.LogError(ex, "Задача {JobId}: Ошибка на этапе скачивания.", jobId);
                            await conversionLogger.LogErrorAsync(jobId, $"Ошибка при скачивании видео: {ex.Message}", ex.StackTrace, ConversionStatus.Failed);
                            await DbJobManager.UpdateJobStatusAsync(jobRepository, jobId, ConversionStatus.Failed, errorMessage: $"Ошибка скачивания: {ex.Message}");
                            // Удаляем временный файл, если он был создан
                             if (!string.IsNullOrEmpty(videoPath) && File.Exists(videoPath))
                            {
                                CleanupFile(tempFileManager, videoPath, logger, jobId);
                            }
                        }
                    } // Конец using scope - все Scoped зависимости уничтожаются
                }
                catch (OperationCanceledException)
                {
                    _logger.LogInformation("DownloadWorker остановлен из-за токена отмены.");
                    break; // Выход из цикла while
                }
                catch (Exception ex)
                {
                    // Ошибка чтения из канала или создания scope - это серьезно
                    _logger.LogCritical(ex, "Критическая ошибка в DownloadBackgroundService WorkerLoop.");
                    // Можно добавить задержку перед следующей попыткой чтения
                    await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken); 
                }
            }
        }

        private bool IsInstagramUrl(string url)
        {
            if (string.IsNullOrEmpty(url)) return false;
            return url.Contains("instagram.com") || url.Contains("cdninstagram.com") || url.Contains("fbcdn.net");
        }

        private void CleanupFile(ITempFileManager tempFileManager, string path, ILogger logger, string jobId)
        {
            if (!string.IsNullOrEmpty(path) && File.Exists(path))
            {
                try
                {
                    tempFileManager.DeleteTempFile(path);
                    logger.LogInformation("Задача {JobId}: Временный файл {Path} удален.", jobId, path);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Задача {JobId}: Ошибка при удалении временного файла: {Path}", jobId, path);
                    // Пытаемся залогировать и в основной лог задачи
                    using var cleanupScope = _serviceProvider.CreateScope();
                    var conversionLogger = cleanupScope.ServiceProvider.GetRequiredService<IConversionLogger>();
                    conversionLogger.LogWarningAsync(jobId, $"Ошибка при удалении временного файла: {path}", ex.Message).GetAwaiter().GetResult();
                }
            }
        }

        public override Task StopAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("DownloadBackgroundService останавливается.");
            // Можно добавить логику для graceful shutdown, если необходимо
            return base.StopAsync(cancellationToken);
        }
    }
} 