using FileConverter.Data;
using FileConverter.Models;
using FileConverter.Services.Interfaces;
using Polly;
using System.Net;
using FileConverter.Services;

namespace FileConverter.Services.BackgroundServices
{
    /// <summary>
    /// Фоновый сервис для скачивания видео из очереди DownloadChannel.
    /// </summary>
    public class DownloadBackgroundService : BackgroundService
    {
        private readonly ILogger<DownloadBackgroundService> _logger;
        private readonly IServiceProvider _serviceProvider;
        private readonly ProcessingChannels _channels;
        private readonly MetricsCollector _metricsCollector;
        private readonly int _maxConcurrentDownloads;

        public DownloadBackgroundService(
            ILogger<DownloadBackgroundService> logger,
            IServiceProvider serviceProvider,
            ProcessingChannels channels,
            MetricsCollector metricsCollector,
            IConfiguration configuration)
        {
            _logger = logger;
            _serviceProvider = serviceProvider;
            _channels = channels;
            _metricsCollector = metricsCollector;
            // Получаем максимальное количество параллельных загрузок из конфигурации
            _maxConcurrentDownloads = configuration.GetValue("Performance:MaxConcurrentDownloads", 5); 
            // Логирование инициализации убрано для уменьшения количества логов
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {

            // Создаем ограниченное количество параллельных задач для загрузки
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
                            
                            // Атомарно обновляем статус с проверкой, что задача еще в статусе Pending
                            bool statusUpdated = await jobRepository.TryUpdateJobStatusIfAsync(jobId, ConversionStatus.Pending, ConversionStatus.Downloading);
                            if (!statusUpdated)
                            {
                                logger.LogInformation("Задача {JobId} уже не в статусе Pending, пропускаем обработку (возможно уже обрабатывается).", jobId);
                                continue; // Переходим к следующей итерации
                            }
                            
                            queueTimeMs = (long)(DateTime.UtcNow - job.CreatedAt).TotalMilliseconds;
                            await conversionLogger.LogDownloadStartedAsync(jobId, videoUrl, queueTimeMs);
                            await conversionLogger.LogStatusChangedAsync(jobId, ConversionStatus.Downloading);

                            // Запускаем таймер для метрик загрузки
                            _metricsCollector.StartTimer("download_video", jobId);

                            // Скачиваем видео
                            byte[] fileData;
                            string sourceDescription;
                            var startTime = DateTime.UtcNow; // Время начала для всех типов загрузки

                                                    // Пытаемся скачать из S3 одним запросом
                        fileData = await storageService.TryDownloadFileAsync(videoUrl);
                        if (fileData != null)
                        {
                            // Файл найден в S3 - создаем временный файл и сохраняем данные
                            sourceDescription = "S3 хранилища";
                            videoPath = tempFileManager.CreateTempFile(".mp4");
                            await File.WriteAllBytesAsync(videoPath, fileData, stoppingToken);
                            
                            await conversionLogger.LogSystemInfoAsync($"Видео для {jobId} найдено в S3: {videoUrl}");
                            
                            // Для S3 файлов вычисляем реальную скорость (файл уже в памяти, загрузка мгновенная)
                            var s3ElapsedSeconds = (DateTime.UtcNow - startTime).TotalSeconds;
                            var s3SpeedBytesPerSecond = s3ElapsedSeconds > 0 ? fileData.Length / s3ElapsedSeconds : fileData.Length;
                            await conversionLogger.LogDownloadProgressAsync(jobId, fileData.Length, fileData.Length, s3SpeedBytesPerSecond);
                            
                            logger.LogInformation("Задача {JobId}: видео найдено в S3, сохранено во временный файл {VideoPath}", jobId, videoPath);
                        }
                        else
                        {
                            sourceDescription = $"URL ({ (IsInstagramUrl(videoUrl) ? "instagram-downloader" : "default") })";
                            logger.LogInformation("Задача {JobId}: скачивание из {SourceDescription}: {VideoUrl}", jobId, sourceDescription, videoUrl);
                            
                            // Настройка Polly retry policy
                            var retryPolicy = Policy
                                .Handle<Exception>(ex =>
                                    !(ex is OperationCanceledException && stoppingToken.IsCancellationRequested) &&
                                    ex is not ReelsDownloadProhibitedException)
                                .WaitAndRetryAsync(
                                    retryCount: 3,
                                    sleepDurationProvider: retryAttempt => TimeSpan.FromSeconds(Math.Pow(2, retryAttempt)), // 2, 4, 8 секунд
                                    onRetry: (outcome, timespan, retryCount, context) =>
                                    {
                                        logger.LogWarning("Задача {JobId}: Попытка {RetryCount}/3 загрузки неудачна. Повтор через {Delay}с. Ошибка: {Error}", 
                                            jobId, retryCount, timespan.TotalSeconds, outcome?.Message ?? "Unknown");
                                        
                                        // Очищаем частично загруженный файл перед повтором
                                        if (!string.IsNullOrEmpty(videoPath) && File.Exists(videoPath))
                                        {
                                            try { File.Delete(videoPath); logger.LogDebug("Удален частично загруженный файл: {VideoPath}", videoPath); } 
                                            catch (Exception cleanupEx) { logger.LogWarning("Не удалось удалить файл {VideoPath}: {Error}", videoPath, cleanupEx.Message); }
                                            videoPath = string.Empty;
                                        }
                                    });

                            // Выполняем загрузку с retry
                            await retryPolicy.ExecuteAsync(async () =>
                            {
                                logger.LogInformation("Задача {JobId}: начинаем попытку загрузки", jobId);
                                
                                using var request = new HttpRequestMessage(HttpMethod.Get, videoUrl);
                                using var response = await instagramHttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, stoppingToken);

                                if (response.StatusCode == System.Net.HttpStatusCode.Forbidden)
                                    throw new InvalidOperationException($"Доступ запрещен (403 Forbidden). URL может требовать авторизации или не поддерживает прямое скачивание: {videoUrl}");
                                if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                                    throw new InvalidOperationException($"Файл не найден (404 Not Found): {videoUrl}");
                                if (response.StatusCode == HttpStatusCode.ServiceUnavailable)
                                    throw new ReelsDownloadProhibitedException("Reels имеет запрет на скачивание");
                                if (!response.IsSuccessStatusCode)
                                    throw new InvalidOperationException($"HTTP ошибка при скачивании: {(int)response.StatusCode} {response.ReasonPhrase}");

                                // Создаем временный файл сразу для потоковой загрузки
                                videoPath = tempFileManager.CreateTempFile(".mp4");
                                logger.LogInformation("Задача {JobId}: создан временный файл {VideoPath}", jobId, videoPath);

                                // Streaming загрузка без загрузки в память
                                using (var fileStream = File.Create(videoPath))
                                using (var httpStream = await response.Content.ReadAsStreamAsync(stoppingToken))
                                {
                                    // Добавляем дополнительный timeout для streaming операций
                                    using var timeoutCts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
                                    using var combinedCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, timeoutCts.Token);
                                    
                                    // Логирование прогресса загрузки
                                    var startTime = DateTime.UtcNow;
                                    long totalBytesDownloaded = 0;
                                    var buffer = new byte[81920]; // 80KB буфер
                                    int bytesRead;
                                    var lastLogTime = DateTime.UtcNow;
                                    
                                    logger.LogInformation("Задача {JobId}: начата потоковая загрузка файла", jobId);
                                    
                                    try
                                    {
                                        while ((bytesRead = await httpStream.ReadAsync(buffer, 0, buffer.Length, combinedCts.Token)) > 0)
                                        {
                                            await fileStream.WriteAsync(buffer, 0, bytesRead, combinedCts.Token);
                                            totalBytesDownloaded += bytesRead;
                                            
                                            // Логируем прогресс каждые 15 секунд (вместо 5) и передаем скорость в БД
                                            if ((DateTime.UtcNow - lastLogTime).TotalSeconds >= 15)
                                            {
                                                var downloadedMB = totalBytesDownloaded / (1024.0 * 1024.0);
                                                var elapsedSeconds = (DateTime.UtcNow - startTime).TotalSeconds;
                                                var speedMBps = downloadedMB / elapsedSeconds;
                                                var speedBytesPerSecond = speedMBps * 1024 * 1024; // Конвертируем в байты/сек
                                                
                                                // Логируем в консоль
                                                logger.LogInformation("Задача {JobId}: загружено {DownloadedMB:F2} МБ, скорость {Speed:F2} МБ/с", 
                                                    jobId, downloadedMB, speedMBps);
                                                
                                                // Передаем прогресс и скорость в БД через ConversionLogger
                                                await conversionLogger.LogDownloadProgressAsync(jobId, totalBytesDownloaded, null, speedBytesPerSecond);
                                                    
                                                lastLogTime = DateTime.UtcNow;
                                            }
                                        }
                                        
                                        await fileStream.FlushAsync(combinedCts.Token);
                                        
                                        var finalSizeMB = totalBytesDownloaded / (1024.0 * 1024.0);
                                        var totalElapsedSeconds = (DateTime.UtcNow - startTime).TotalSeconds;
                                        logger.LogInformation("Задача {JobId}: загрузка завершена, {FinalSize:F2} МБ за {ElapsedSeconds:F1} сек", 
                                            jobId, finalSizeMB, totalElapsedSeconds);
                                    }
                                    catch (OperationCanceledException) when (timeoutCts.Token.IsCancellationRequested)
                                    {
                                        logger.LogError("Задача {JobId}: превышен таймаут загрузки (3 мин), загружено {DownloadedMB:F2} МБ", 
                                            jobId, totalBytesDownloaded / (1024.0 * 1024.0));
                                        throw new TimeoutException($"Превышен таймаут (3 мин) при загрузке файла: {videoUrl}");
                                    }
                                }

                                var fileInfo = new FileInfo(videoPath);
                                var fileSizeBytes = fileInfo.Length;
                                
                                await conversionLogger.LogSystemInfoAsync($"Видео для {jobId} скачано по {sourceDescription}: {videoUrl}");
                                
                                // Логируем финальный прогресс с итоговой скоростью
                                var finalElapsedSeconds = (DateTime.UtcNow - startTime).TotalSeconds;
                                var finalSpeedMBps = (fileSizeBytes / (1024.0 * 1024.0)) / finalElapsedSeconds;
                                var finalSpeedBytesPerSecond = finalSpeedMBps * 1024 * 1024;
                                await conversionLogger.LogDownloadProgressAsync(jobId, fileSizeBytes, fileSizeBytes, finalSpeedBytesPerSecond);
                                
                                // Читаем файл для вычисления хеша
                                fileData = await File.ReadAllBytesAsync(videoPath, stoppingToken);
                                
                                logger.LogInformation("Задача {JobId}: успешная загрузка файла", jobId);
                            });
                        }
                            await conversionLogger.LogDownloadCompletedAsync(jobId, fileData.Length, videoPath);

                            // Вычисляем хеш видео по содержимому файла
                            // Для всех файлов (включая S3) используем хеширование содержимого для корректного кэширования
                            string videoHash = VideoHasher.GetHash(fileData);
                            logger.LogInformation("Задача {JobId}: хеш видео {VideoHash} (источник: {Source})", jobId, videoHash, sourceDescription);

                            // Проверяем кэш по реальному хешу файла
                            var mediaItemRepository = scope.ServiceProvider.GetRequiredService<IMediaItemRepository>();
                            try
                            {
                                var existingItem = await mediaItemRepository.FindByVideoHashAsync(videoHash);
                                if (existingItem != null && !string.IsNullOrEmpty(existingItem.AudioUrl))
                                {
                                    logger.LogInformation("Задача {JobId}: Найден кэш по хешу файла {VideoHash}. URL: {AudioUrl}", 
                                        jobId, videoHash, existingItem.AudioUrl);
                                    await conversionLogger.LogCacheHitAsync(jobId, existingItem.AudioUrl, videoHash);
                                    
                                    // Обновляем статус задачи на Completed с данными из кэша
                                    await DbJobManager.UpdateJobStatusAsync(jobRepository, jobId, ConversionStatus.Completed, 
                                        mp3Url: existingItem.AudioUrl, 
                                        newVideoUrl: existingItem.VideoUrl);
                                    
                                    // Сохраняем ключевые кадры если они есть
                                    if (existingItem.Keyframes != null && existingItem.Keyframes.Count > 0)
                                    {
                                        await jobRepository.UpdateJobKeyframesAsync(jobId, existingItem.Keyframes);
                                        logger.LogInformation("Задача {JobId}: Сохранены ключевые кадры из кэша: {KeyframeCount} кадров", 
                                            jobId, existingItem.Keyframes.Count);
                                    }
                                    
                                    // Сохраняем данные анализа аудио если они есть
                                    if (existingItem.AudioAnalysis != null)
                                    {
                                        await jobRepository.UpdateJobAudioAnalysisAsync(jobId, existingItem.AudioAnalysis);
                                        logger.LogInformation("Задача {JobId}: Сохранены данные анализа аудио из кэша: BPM {Bpm}", 
                                            jobId, existingItem.AudioAnalysis.tempo_bpm);
                                    }
                                    
                                    // Останавливаем таймер для метрик (кэш-попадание)
                                    _metricsCollector.StopTimer("download_video", jobId, isSuccess: true);
                                    
                                    // Очищаем временный файл
                                    CleanupFile(tempFileManager, videoPath, logger, jobId);
                                    
                                    continue; // Переходим к следующей задаче
                                }
                            }
                            catch (Exception cacheEx)
                            {
                                logger.LogError(cacheEx, "Задача {JobId}: Ошибка при проверке кэша по хешу {VideoHash}", jobId, videoHash);
                                // Продолжаем обработку без кэша
                            }

                            // Останавливаем таймер для метрик (успешная загрузка)
                            _metricsCollector.StopTimer("download_video", jobId, isSuccess: true);

                            // Помещаем задачу в очередь конвертации
                            bool conversionQueueSuccess = _channels.ConversionChannel.Writer.TryWrite((jobId, videoPath, videoHash));
                            if (conversionQueueSuccess)
                            {
                                logger.LogInformation("Задача {JobId}: передана в очередь конвертации (файл {VideoPath}, хеш {VideoHash})", jobId, videoPath, videoHash);
                                await conversionLogger.LogSystemInfoAsync($"Задание {jobId} добавлено в очередь на конвертацию с хешем видео: {videoHash}");
                            }
                            else
                            {
                                logger.LogWarning("Задача {JobId}: очередь конвертации переполнена, файлы будут очищены", jobId);
                                await conversionLogger.LogErrorAsync(jobId, "Очередь конвертации переполнена", null, ConversionStatus.Failed);
                                await DbJobManager.UpdateJobStatusAsync(jobRepository, jobId, ConversionStatus.Failed, errorMessage: "Очередь конвертации переполнена");
                                // Очищаем временный файл, так как он не будет обработан
                                CleanupFile(tempFileManager, videoPath, logger, jobId);
                            }
                            
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
                        catch (TimeoutException timeoutEx)
                        {
                            // Специальная обработка таймаутов загрузки
                            logger.LogError(timeoutEx, "Задача {JobId}: Превышен таймаут загрузки: {Message}", jobId, timeoutEx.Message);
                            
                            // Останавливаем таймер для метрик (таймаут)
                            _metricsCollector.StopTimer("download_video", jobId, isSuccess: false);
                            
                            await conversionLogger.LogErrorAsync(jobId, $"Таймаут загрузки: {timeoutEx.Message}", timeoutEx.StackTrace, ConversionStatus.Failed);
                            await DbJobManager.UpdateJobStatusAsync(jobRepository, jobId, ConversionStatus.Failed, errorMessage: $"Таймаут загрузки: {timeoutEx.Message}");
                            
                            // Удаляем частично загруженный файл
                            if (!string.IsNullOrEmpty(videoPath) && File.Exists(videoPath))
                            {
                                CleanupFile(tempFileManager, videoPath, logger, jobId);
                            }
                        }
                        catch (ReelsDownloadProhibitedException reelsEx)
                        {
                            // Специальная обработка 503 для Reels: помечаем задачу как Failed с заданным текстом
                            logger.LogWarning(reelsEx, "Задача {JobId}: Reels запрещен к скачиванию.", jobId);

                            _metricsCollector.StopTimer("download_video", jobId, isSuccess: false);

                            await conversionLogger.LogErrorAsync(jobId, reelsEx.Message, reelsEx.StackTrace, ConversionStatus.Failed);
                            await DbJobManager.UpdateJobStatusAsync(jobRepository, jobId, ConversionStatus.Failed, errorMessage: reelsEx.Message);

                            if (!string.IsNullOrEmpty(videoPath) && File.Exists(videoPath))
                            {
                                CleanupFile(tempFileManager, videoPath, logger, jobId);
                            }
                        }
                        catch (Exception ex)
                        {
                            // Логируем ошибку и обновляем статус задачи на Failed
                            logger.LogError(ex, "Задача {JobId}: Ошибка на этапе скачивания.", jobId);
                            
                            // Останавливаем таймер для метрик (неуспешная загрузка)
                            _metricsCollector.StopTimer("download_video", jobId, isSuccess: false);
                            
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