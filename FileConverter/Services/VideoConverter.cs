using FileConverter.Data;
using FileConverter.Models;
using System.Threading.Channels;
using Xabe.FFmpeg;
using System.Security.Cryptography;
using System.Text;
using FileConverter.Services.Interfaces;

namespace FileConverter.Services;

/// <summary>
/// Сервис для конвертации видео в аудио
/// </summary>
public class VideoConverter : IVideoConverter
{
    private readonly IS3StorageService _storageService;
    private readonly IMediaItemRepository _mediaItemRepository;
    private readonly ILogger<VideoConverter> _logger;
    private readonly ITempFileManager _tempFileManager;
    private readonly IJobRepository _repository;
    private readonly UrlValidator _urlValidator;
    private readonly HttpClient _httpClient;
    private readonly HttpClient _instagramHttpClient;
    private readonly IConversionLogger _conversionLogger;
    
    // Очередь для загрузки видео с ограничением параллельных задач
    private static readonly Channel<(string JobId, string VideoUrl)> _downloadChannel = 
        Channel.CreateBounded<(string, string)>(new BoundedChannelOptions(100)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleWriter = false,
            SingleReader = false
        });
        
    // Очередь для конвертации видео
    private static readonly Channel<(string JobId, string VideoPath, string VideoHash)> _conversionChannel = 
        Channel.CreateBounded<(string, string, string)>(new BoundedChannelOptions(Environment.ProcessorCount)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleWriter = false,
            SingleReader = false
        });
    
    // Очередь для загрузки MP3
    private static readonly Channel<(string JobId, string Mp3Path, string VideoPath, string VideoHash)> _uploadChannel = 
        Channel.CreateBounded<(string, string, string, string)>(new BoundedChannelOptions(10)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleWriter = false,
            SingleReader = false
        });
    
    // Флаги запуска воркеров
    private static bool _downloadWorkersStarted = false;
    private static bool _conversionWorkersStarted = false;
    private static bool _uploadWorkersStarted = false;
    private static readonly object _syncLock = new();

    /// <summary>
    /// Создает новый экземпляр конвертера видео
    /// </summary>
    public VideoConverter(
        IS3StorageService storageService,
        IMediaItemRepository mediaItemRepository,
        ILogger<VideoConverter> logger,
        ITempFileManager tempFileManager,
        IJobRepository repository,
        UrlValidator urlValidator,
        HttpClient httpClient,
        IHttpClientFactory httpClientFactory,
        IConversionLogger conversionLogger)
    {
        _storageService = storageService;
        _mediaItemRepository = mediaItemRepository;
        _logger = logger;
        _tempFileManager = tempFileManager;
        _repository = repository;
        _urlValidator = urlValidator;
        _httpClient = httpClient;
        _instagramHttpClient = httpClientFactory.CreateClient("instagram-downloader");
        _conversionLogger = conversionLogger;
        
        // Инициализация обработчиков, если они еще не запущены
        StartWorkers();
    }

    /// <summary>
    /// Запускает процесс обработки видео по идентификатору задачи
    /// </summary>
    /// <param name="jobId">Идентификатор задачи конвертации</param>
    public async Task ProcessVideo(string jobId)
    {
        try
        {
            // Находим задачу в базе данных
            var job = await _repository.GetJobByIdAsync(jobId);
            if (job == null)
            {
                await _conversionLogger.LogErrorAsync(jobId, $"Задание с ID {jobId} не найдено");
                return;
            }

            await _conversionLogger.LogJobQueuedAsync(jobId, job.VideoUrl, "Задание добавлено в очередь");

            // Получаем хеш для поиска в репозитории
            string videoHash = string.Empty;
            using (var sha = SHA256.Create())
            {
                var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(job.VideoUrl));
                videoHash = Convert.ToBase64String(hash);
            }

            // Проверяем наличие в репозитории вместо кэша
            var existingItem = await _mediaItemRepository.FindByVideoHashAsync(videoHash);
            if (existingItem != null && !string.IsNullOrEmpty(existingItem.AudioUrl))
            {
                await _conversionLogger.LogCacheHitAsync(jobId, existingItem.AudioUrl, videoHash);
                await DbJobManager.UpdateJobStatusAsync(_repository, jobId, ConversionStatus.Completed, mp3Url: existingItem.AudioUrl);
                return;
            }

            // Проверяем безопасность URL
            if (!_urlValidator.IsUrlValid(job.VideoUrl))
            {
                await _conversionLogger.LogWarningAsync(jobId, "Обнаружен небезопасный URL", job.VideoUrl);
                await DbJobManager.UpdateJobStatusAsync(_repository, jobId, ConversionStatus.Failed, 
                    errorMessage: "Обнаружен недопустимый или небезопасный URL");
                return;
            }
            
            // Проверяем размер файла
            //if (!await _urlValidator.IsFileSizeValid(job.VideoUrl))
            //{
            //    await _conversionLogger.LogWarningAsync(jobId, "Размер файла превышает допустимый", job.VideoUrl);
            //    await DbJobManager.UpdateJobStatusAsync(_repository, jobId, ConversionStatus.Failed, 
            //        errorMessage: "Размер файла превышает максимально допустимый");
            //    return;
            //}

            // Помещаем видео в очередь загрузки
            await _downloadChannel.Writer.WriteAsync((jobId, job.VideoUrl));
            await _conversionLogger.LogSystemInfoAsync($"Задание {jobId} добавлено в очередь на скачивание");
        }
        catch (Exception ex)
        {
            await _conversionLogger.LogErrorAsync(jobId, $"Ошибка при постановке задания в очередь: {ex.Message}", ex.StackTrace);
            
            // Обновляем статус задачи в случае ошибки
            await DbJobManager.UpdateJobStatusAsync(_repository, jobId, ConversionStatus.Failed, 
                errorMessage: $"Ошибка при постановке задания в очередь: {ex.Message}");
        }
    }

    /// <summary>
    /// Метод для запуска рабочих потоков обработки
    /// </summary>
    private void StartWorkers()
    {
        lock (_syncLock)
        {
            // Запуск обработчиков загрузки видео
            if (!_downloadWorkersStarted)
            {
                _downloadWorkersStarted = true;
                int downloadWorkers = 10; // Максимум 10 параллельных загрузок
                
                for (int i = 0; i < downloadWorkers; i++)
                {
                    Task.Factory.StartNew(DownloadWorker, 
                        TaskCreationOptions.LongRunning);
                }
                
                _conversionLogger.LogSystemInfoAsync($"Запущено {downloadWorkers} рабочих потоков для скачивания видео").GetAwaiter().GetResult();
            }
            
            // Запуск обработчиков конвертации
            if (!_conversionWorkersStarted)
            {
                _conversionWorkersStarted = true;
                int conversionWorkers = Math.Max(1, Environment.ProcessorCount - 1); // Оставляем 1 ядро для системы
                
                for (int i = 0; i < conversionWorkers; i++)
                {
                    Task.Factory.StartNew(ConversionWorker, 
                        TaskCreationOptions.LongRunning);
                }
                
                _conversionLogger.LogSystemInfoAsync($"Запущено {conversionWorkers} рабочих потоков для конвертации видео").GetAwaiter().GetResult();
            }
            
            // Запуск обработчиков загрузки MP3
            if (!_uploadWorkersStarted)
            {
                _uploadWorkersStarted = true;
                int uploadWorkers = 5; // Максимум 5 параллельных загрузок
                
                for (int i = 0; i < uploadWorkers; i++)
                {
                    Task.Factory.StartNew(UploadWorker, 
                        TaskCreationOptions.LongRunning);
                }
                
                _conversionLogger.LogSystemInfoAsync($"Запущено {uploadWorkers} рабочих потоков для загрузки MP3").GetAwaiter().GetResult();
            }
        }
    }
    
    /// <summary>
    /// Воркер для загрузки видео
    /// </summary>
    private async Task DownloadWorker()
    {
        while (await _downloadChannel.Reader.WaitToReadAsync())
        {
            string jobId = string.Empty;
            string videoUrl = string.Empty;
            string videoPath = string.Empty;
            DateTime queueStart = DateTime.UtcNow;
            
            try
            {
                // Извлекаем информацию о задаче из очереди
                var item = await _downloadChannel.Reader.ReadAsync();
                jobId = item.JobId;
                videoUrl = item.VideoUrl;
                
                // Рассчитываем время ожидания в очереди
                var job = await _repository.GetJobByIdAsync(jobId);
                long queueTimeMs = 0;
                
                if (job != null && job.CreatedAt != null)
                {
                    queueTimeMs = (long)(DateTime.UtcNow - job.CreatedAt).TotalMilliseconds;
                }
                
                await _conversionLogger.LogDownloadStartedAsync(jobId, videoUrl, queueTimeMs);
                
                try
                {
                    // Обновляем статус
                    await DbJobManager.UpdateJobStatusAsync(_repository, jobId, ConversionStatus.Downloading);
                    await _conversionLogger.LogStatusChangedAsync(jobId, ConversionStatus.Downloading);
                }
                catch (Exception statusEx)
                {
                    await _conversionLogger.LogErrorAsync(jobId, $"Ошибка обновления статуса: {statusEx.Message}", statusEx.StackTrace);
                }

                // Временный путь
                videoPath = _tempFileManager.CreateTempFile( ".mp4");
                
                // Скачиваем видео
                byte[] fileData;
                
                // Если URL уже из нашего хранилища, используем сервис хранилища
                if (await _storageService.FileExistsAsync(videoUrl))
                {
                    fileData = await _storageService.DownloadFileAsync(videoUrl);
                    await _conversionLogger.LogSystemInfoAsync($"Видео скачано из хранилища: {videoUrl}");
                    
                    // Логируем прогресс загрузки
                    await _conversionLogger.LogDownloadProgressAsync(jobId, fileData.Length, fileData.Length);
                }
                else
                {
                    try
                    {
                        using var request = new HttpRequestMessage(HttpMethod.Get, videoUrl);
                        using var response = await _instagramHttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);

                        // Обрабатываем различные коды ошибок
                        if (response.StatusCode == System.Net.HttpStatusCode.Forbidden)
                        {
                            throw new InvalidOperationException($"Доступ запрещен (403 Forbidden). URL может требовать авторизации или не поддерживает прямое скачивание: {videoUrl}");
                        }
                        else if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                        {
                            throw new InvalidOperationException($"Файл не найден (404 Not Found): {videoUrl}");
                        }
                        else if (!response.IsSuccessStatusCode)
                        {
                            throw new InvalidOperationException($"HTTP ошибка при скачивании: {(int)response.StatusCode} {response.ReasonPhrase}");
                        }
                        
                        fileData = await response.Content.ReadAsByteArrayAsync();
                        string clientType = IsInstagramUrl(videoUrl) ? "прокси-клиент Instagram" : "стандартный клиент";
                        await _conversionLogger.LogSystemInfoAsync($"Видео скачано по URL: {videoUrl} используя {clientType}");
                        
                        // Логируем прогресс загрузки - для внешних URL у нас только финальный результат
                        await _conversionLogger.LogDownloadProgressAsync(jobId, fileData.Length, fileData.Length);
                    }
                    catch (HttpRequestException httpEx)
                    {
                        if (httpEx.Message.Contains("403"))
                        {
                            throw new InvalidOperationException($"Доступ запрещен (403 Forbidden). URL требует авторизации: {videoUrl}", httpEx);
                        }
                        else
                        {
                            throw new InvalidOperationException($"HTTP ошибка при скачивании: {httpEx.Message}", httpEx);
                        }
                    }
                }
                
                await File.WriteAllBytesAsync(videoPath, fileData);
                
                // Логируем завершение загрузки
                await _conversionLogger.LogDownloadCompletedAsync(jobId, fileData.Length, videoPath);
                
                // Вычисляем хеш видео
                string videoHash = VideoHasher.GetHash(fileData);
                try
                {
                    // Обновляем задачу с размером файла и типом контента
                    job = await _repository.GetJobByIdAsync(jobId);
                    if (job != null)
                    {
                        job.FileSizeBytes = fileData.Length;
                        job.TempVideoPath = videoPath;
                        job.VideoHash = videoHash;
                        await _repository.UpdateJobAsync(job);
                    }
                }
                catch (Exception updateEx)
                {
                    await _conversionLogger.LogErrorAsync(jobId, $"Ошибка обновления информации о файле: {updateEx.Message}", updateEx.StackTrace);
                }

                // Проверяем наличие в C3 хранилище
                var existingItem = await _mediaItemRepository.FindByVideoHashAsync(videoHash);
                if (existingItem != null && !string.IsNullOrEmpty(existingItem.AudioUrl))
                {
                    await _conversionLogger.LogCacheHitAsync(jobId, existingItem.AudioUrl, videoHash);
                    
                    // Обновляем статус задачи
                    await DbJobManager.UpdateJobStatusAsync(_repository, jobId, ConversionStatus.Completed, 
                        mp3Url: existingItem.AudioUrl, newVideoUrl: existingItem.VideoUrl);
                    
                    // Логируем успешное завершение задачи
                    var totalTimeMs = (long)(DateTime.UtcNow - queueStart).TotalMilliseconds;
                    await _conversionLogger.LogJobCompletedAsync(jobId, existingItem.AudioUrl, totalTimeMs);
                    
                    return;
                }
                
                // Помещаем видео в очередь конвертации
                await _conversionChannel.Writer.WriteAsync((jobId, videoPath, videoHash));
                await _conversionLogger.LogSystemInfoAsync($"Задание {jobId} добавлено в очередь на конвертацию с хешем видео: {videoHash}");
            }
            catch (Exception ex)
            {
                await _conversionLogger.LogErrorAsync(jobId, $"Ошибка при скачивании видео: {ex.Message}", ex.StackTrace, ConversionStatus.Failed);
                
                // Обновляем статус задачи в случае ошибки
                await DbJobManager.UpdateJobStatusAsync(_repository, jobId, ConversionStatus.Failed, 
                    errorMessage: $"Ошибка при скачивании видео: {ex.Message}");
                
                // Удаляем временный файл если он был создан
                if (!string.IsNullOrEmpty(videoPath) && File.Exists(videoPath))
                {
                    _tempFileManager.DeleteTempFile(videoPath);
                }
            }
        }
    }
    
    /// <summary>
    /// Проверяет, является ли URL адресом из Instagram
    /// </summary>
    private bool IsInstagramUrl(string url)
    {
        if (string.IsNullOrEmpty(url))
            return false;
            
        return url.Contains("instagram.com") || 
               url.Contains("cdninstagram.com") || 
               url.Contains("fbcdn.net");
    }
    
    /// <summary>
    /// Воркер для конвертации видео
    /// </summary>
    private async Task ConversionWorker()
    {
        while (await _conversionChannel.Reader.WaitToReadAsync())
        {
            string jobId = string.Empty;
            string videoPath = string.Empty;
            string mp3Path = string.Empty;
            string videoHash = null;
            DateTime queueStart = DateTime.UtcNow;
            
            try
            {
                // Извлекаем информацию о задаче из очереди
                var item = await _conversionChannel.Reader.ReadAsync();
                jobId = item.JobId;
                videoPath = item.VideoPath;
                videoHash = item.VideoHash;
                
                // Рассчитываем время ожидания в очереди
                var job = await _repository.GetJobByIdAsync(jobId);
                long queueTimeMs = 0;
                
                if (job != null && job.LastAttemptAt.HasValue)
                {
                    queueTimeMs = (long)(DateTime.UtcNow - job.LastAttemptAt.Value).TotalMilliseconds;
                }
                
                await _conversionLogger.LogConversionStartedAsync(jobId, queueTimeMs, $"Хеш видео: {videoHash}");
                
                // Обновляем статус
                await DbJobManager.UpdateJobStatusAsync(_repository, jobId, ConversionStatus.Converting);
                await _conversionLogger.LogStatusChangedAsync(jobId, ConversionStatus.Converting);
                
                // Создаем выходной путь
                mp3Path = _tempFileManager.CreateTempFile(".mp3");
                
                // Обновляем задачу с путями к временным файлам
                var conversionJob = await _repository.GetJobByIdAsync(jobId);
                if (conversionJob != null)
                {
                    conversionJob.TempVideoPath = videoPath;
                    conversionJob.TempMp3Path = mp3Path;
                    await _repository.UpdateJobAsync(conversionJob);
                }
                
                // Получаем информацию о видео
                var mediaInfo = await FFmpeg.GetMediaInfo(videoPath);
                
                // Проверяем наличие аудиопотока
                if (mediaInfo.AudioStreams?.Count() == 0)
                {
                    throw new InvalidOperationException("Аудиопоток не найден в видеофайле");
                }
                
                // Конвертируем видео в MP3
                var conversion = FFmpeg.Conversions.New()
                    .AddStream(mediaInfo.AudioStreams)
                    .SetOutputFormat("mp3")
                    .SetAudioBitrate(128000)
                    .SetOutput(mp3Path);
                
                await _conversionLogger.LogSystemInfoAsync($"Запуск FFmpeg: {conversion.Build()}");
                
                // Регистрируем обработчик для отслеживания прогресса
                conversion.OnProgress += async (sender, args) => {
                    // Логируем прогресс конвертации
                    await _conversionLogger.LogConversionProgressAsync(jobId, args.Percent, args.TotalLength.TotalSeconds - args.Duration.TotalSeconds);
                };
                
                // Запускаем и отслеживаем процесс конвертации
                await conversion.Start();
                
                await _conversionLogger.LogSystemInfoAsync($"Конвертация завершена для задания {jobId}");
                
                // Проверяем существование файла
                if (!File.Exists(mp3Path))
                {
                    throw new InvalidOperationException("Конвертация завершена, но MP3 файл не найден");
                }
                
                // Получаем информацию о файле
                var fileInfo = new FileInfo(mp3Path);
                var mp3FileSize = fileInfo.Length;
                
                // Логируем завершение конвертации
                await _conversionLogger.LogConversionCompletedAsync(jobId, mp3FileSize, mediaInfo.Duration.TotalSeconds, mp3Path);
                
                // Помещаем в очередь загрузки MP3
                await _uploadChannel.Writer.WriteAsync((jobId, mp3Path, videoPath, videoHash));
                await _conversionLogger.LogSystemInfoAsync($"Задание {jobId} добавлено в очередь на загрузку MP3");
            }
            catch (Exception ex)
            {
                await _conversionLogger.LogErrorAsync(jobId, $"Ошибка при конвертации видео: {ex.Message}", ex.StackTrace, ConversionStatus.Failed);
                
                // Обновляем статус задачи в случае ошибки
                await DbJobManager.UpdateJobStatusAsync(_repository, jobId, ConversionStatus.Failed, 
                    errorMessage: $"Ошибка при конвертации видео: {ex.Message}");
                
                // Удаляем временные файлы
                CleanupFiles(videoPath, mp3Path);
            }
        }
    }
    
    /// <summary>
    /// Воркер для загрузки MP3 и видео в S3 хранилище
    /// </summary>
    private async Task UploadWorker()
    {
        while (await _uploadChannel.Reader.WaitToReadAsync())
        {
            string jobId = string.Empty;
            string mp3Path = string.Empty;
            string videoPath = string.Empty;
            string videoHash = string.Empty;
            DateTime queueStart = DateTime.UtcNow;
            
            try
            {
                // Извлекаем информацию о задаче из очереди
                var item = await _uploadChannel.Reader.ReadAsync();
                jobId = item.JobId;
                mp3Path = item.Mp3Path;
                videoHash = item.VideoHash;
                videoPath = item.VideoPath;
                
                // Вычисляем время ожидания в очереди
                var job = await _repository.GetJobByIdAsync(jobId);
                long queueTimeMs = 0;
                
                if (job != null && job.LastAttemptAt.HasValue)
                {
                    queueTimeMs = (long)(DateTime.UtcNow - job.LastAttemptAt.Value).TotalMilliseconds;
                }
                
                var fileInfo = new FileInfo(mp3Path);
                var mp3FileSize = fileInfo.Length;
                
                await _conversionLogger.LogUploadStartedAsync(jobId, queueTimeMs, mp3FileSize);
                
                // Обновляем статус
                await DbJobManager.UpdateJobStatusAsync(_repository, jobId, ConversionStatus.Uploading);
                await _conversionLogger.LogStatusChangedAsync(jobId, ConversionStatus.Uploading);
                
                // Получаем задачу
                var uploadJob = await _repository.GetJobByIdAsync(jobId);
                if (uploadJob == null)
                {
                    throw new InvalidOperationException($"Задание {jobId} не найдено");
                }

                // Загружаем видео и MP3 в S3
                var videoUploadTask = _storageService.UploadFileAsync(videoPath, "video/mp4");
                var mp3UploadTask = _storageService.UploadFileAsync(mp3Path, "audio/mpeg");
                
                await Task.WhenAll(videoUploadTask, mp3UploadTask);
                
                var videoUrl = await videoUploadTask;
                var mp3Url = await mp3UploadTask;
                
                // Логируем завершение загрузки
                await _conversionLogger.LogUploadCompletedAsync(jobId, mp3Url);
                
                await _conversionLogger.LogSystemInfoAsync($"Файлы загружены для задания {jobId}. URL видео: {videoUrl}, URL MP3: {mp3Url}");
                
                var mediaItem = new MediaStorageItem
                {
                    VideoHash = videoHash,
                    VideoUrl = videoUrl,
                    AudioUrl = mp3Url,
                    FileSizeBytes = uploadJob.FileSizeBytes ?? 0
                };
                
                var savedItem = await _mediaItemRepository.SaveItemAsync(mediaItem);
                await _conversionLogger.LogSystemInfoAsync($"Медиа элемент сохранен в хранилище C3 с ID: {savedItem.Id}");
                
                await DbJobManager.UpdateJobStatusAsync(_repository, jobId, ConversionStatus.Completed, mp3Url: mp3Url, newVideoUrl: videoUrl);
                await _conversionLogger.LogStatusChangedAsync(jobId, ConversionStatus.Completed);
                
                // Вычисляем общее время выполнения задачи
                var totalTimeMs = 0L;
                if (uploadJob.CreatedAt != null)
                {
                    totalTimeMs = (long)(DateTime.UtcNow - uploadJob.CreatedAt).TotalMilliseconds;
                }
                
                // Логируем успешное завершение задачи
                await _conversionLogger.LogJobCompletedAsync(jobId, mp3Url, totalTimeMs);
                
                await _conversionLogger.LogSystemInfoAsync($"Задание {jobId} успешно завершено");
            }
            catch (Exception ex)
            {
                await _conversionLogger.LogErrorAsync(jobId, $"Ошибка при загрузке MP3: {ex.Message}", ex.StackTrace, ConversionStatus.Failed);
                
                // Обновляем статус задачи в случае ошибки
                await DbJobManager.UpdateJobStatusAsync(_repository, jobId, ConversionStatus.Failed, 
                    errorMessage: $"Ошибка при загрузке MP3: {ex.Message}");
            }
            finally
            {
                // Удаляем временные файлы
                CleanupFiles(videoPath, mp3Path);
            }
        }
    }
    
    /// <summary>
    /// Удаляет временные файлы
    /// </summary>
    private void CleanupFiles(params string[] paths)
    {
        foreach (var path in paths)
        {
            if (!string.IsNullOrEmpty(path) && File.Exists(path))
            {
                try
                {
                    _tempFileManager.DeleteTempFile(path);
                }
                catch (Exception ex)
                {
                    // Используем системный логгер для информационных сообщений, не связанных с конкретным заданием
                    _logger.LogWarning(ex, $"Ошибка при удалении временного файла: {path}");
                    _conversionLogger.LogSystemInfoAsync($"Ошибка при удалении временного файла: {path} - {ex.Message}").GetAwaiter().GetResult();
                }
            }
        }
    }
}
