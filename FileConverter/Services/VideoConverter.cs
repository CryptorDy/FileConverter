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
        IHttpClientFactory httpClientFactory)
    {
        _storageService = storageService;
        _mediaItemRepository = mediaItemRepository;
        _logger = logger;
        _tempFileManager = tempFileManager;
        _repository = repository;
        _urlValidator = urlValidator;
        _httpClient = httpClient;
        _instagramHttpClient = httpClientFactory.CreateClient("instagram-downloader");
        
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
                _logger.LogError($"Task {jobId} not found");
                return;
            }

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
                _logger.LogInformation($"Found existing conversion in storage for task {jobId}: {existingItem.AudioUrl}");
                await DbJobManager.UpdateJobStatusAsync(_repository, jobId, ConversionStatus.Completed, mp3Url: existingItem.AudioUrl);
                return;
            }

            // Проверяем безопасность URL
            if (!_urlValidator.IsUrlValid(job.VideoUrl))
            {
                _logger.LogWarning($"Detected unsafe URL for task {jobId}: {job.VideoUrl}");
                await DbJobManager.UpdateJobStatusAsync(_repository, jobId, ConversionStatus.Failed, 
                    errorMessage: "Detected invalid or unsafe URL");
                return;
            }
            
            // Проверяем размер файла
            //if (!await _urlValidator.IsFileSizeValid(job.VideoUrl))
            //{
            //    _logger.LogWarning($"File exceeds allowed size for task {jobId}: {job.VideoUrl}");
            //    await DbJobManager.UpdateJobStatusAsync(_repository, jobId, ConversionStatus.Failed, 
            //        errorMessage: "File exceeds maximum allowed size");
            //    return;
            //}

            // Помещаем видео в очередь загрузки
            await _downloadChannel.Writer.WriteAsync((jobId, job.VideoUrl));
            _logger.LogInformation($"Task {jobId} queued for download");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Error queuing task {jobId}");
            // Обновляем статус задачи в случае ошибки
            await DbJobManager.UpdateJobStatusAsync(_repository, jobId, ConversionStatus.Failed, 
                errorMessage: $"Error queuing task: {ex.Message}");
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
                
                _logger.LogInformation($"Started {downloadWorkers} video download workers");
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
                
                _logger.LogInformation($"Started {conversionWorkers} video conversion workers");
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
                
                _logger.LogInformation($"Started {uploadWorkers} MP3 upload workers");
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
            
            try
            {
                // Извлекаем информацию о задаче из очереди
                var item = await _downloadChannel.Reader.ReadAsync();
                jobId = item.JobId;
                videoUrl = item.VideoUrl;
                
                _logger.LogInformation($"Starting video download for task {jobId}: {videoUrl}");
                
                try
                {
                    // Обновляем статус
                    await DbJobManager.UpdateJobStatusAsync(_repository, jobId, ConversionStatus.Downloading);
                }
                catch (Exception statusEx)
                {
                    _logger.LogError(statusEx, "Error updating task status {JobId} to Downloading. Continuing processing.", jobId);
                }
                
                // Проверяем тип контента
                var contentTypeResult = await _urlValidator.IsContentTypeValid(videoUrl);
                if (!contentTypeResult.isValid)
                {
                    throw new InvalidOperationException($"Invalid content type: {contentTypeResult.contentType}. Only video files are allowed.");
                }
                
                // Временный путь
                videoPath = _tempFileManager.CreateTempFile( ".mp4");
                
                // Скачиваем видео
                byte[] fileData;
                
                // Если URL уже из нашего хранилища, используем сервис хранилища
                if (await _storageService.FileExistsAsync(videoUrl))
                {
                    fileData = await _storageService.DownloadFileAsync(videoUrl);
                    _logger.LogInformation($"Video downloaded from storage: {videoUrl}");
                }
                else
                {
                    try
                    {
                        // Выбираем правильный HTTP клиент в зависимости от URL
                        var httpClient = IsInstagramUrl(videoUrl) ? _instagramHttpClient : _httpClient;
                        
                        using var request = new HttpRequestMessage(HttpMethod.Get, videoUrl);
                        using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);

                        // Обрабатываем различные коды ошибок
                        if (response.StatusCode == System.Net.HttpStatusCode.Forbidden)
                        {
                            throw new InvalidOperationException($"Access denied (403 Forbidden). URL may require authorization or does not support direct download: {videoUrl}");
                        }
                        else if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                        {
                            throw new InvalidOperationException($"File not found (404 Not Found): {videoUrl}");
                        }
                        else if (!response.IsSuccessStatusCode)
                        {
                            throw new InvalidOperationException($"HTTP error during download: {(int)response.StatusCode} {response.ReasonPhrase}");
                        }
                        
                        fileData = await response.Content.ReadAsByteArrayAsync();
                        _logger.LogInformation($"Video downloaded from URL: {videoUrl} using {(IsInstagramUrl(videoUrl) ? "Instagram proxy client" : "standard client")}");
                    }
                    catch (HttpRequestException httpEx)
                    {
                        if (httpEx.Message.Contains("403"))
                        {
                            throw new InvalidOperationException($"Access denied (403 Forbidden). URL requires authorization: {videoUrl}", httpEx);
                        }
                        else
                        {
                            throw new InvalidOperationException($"HTTP error during download: {httpEx.Message}", httpEx);
                        }
                    }
                }
                
                await File.WriteAllBytesAsync(videoPath, fileData);
                
                // Вычисляем хеш видео
                string videoHash = VideoHasher.GetHash(fileData);
                try
                {
                    // Обновляем задачу с размером файла и типом контента
                    var job = await _repository.GetJobByIdAsync(jobId);
                    if (job != null)
                    {
                        job.FileSizeBytes = fileData.Length;
                        job.ContentType = contentTypeResult.contentType;
                        job.TempVideoPath = videoPath;
                        job.VideoHash = videoHash;
                        await _repository.UpdateJobAsync(job);
                    }
                }
                catch (Exception updateEx)
                {
                    _logger.LogError(updateEx, "Error updating file info for task {JobId}. Continuing processing.", jobId);
                }

                // Проверяем наличие в C3 хранилище
                var existingItem = await _mediaItemRepository.FindByVideoHashAsync(videoHash);
                if (existingItem != null && !string.IsNullOrEmpty(existingItem.AudioUrl))
                {
                    _logger.LogInformation($"Found existing conversion in C3 storage for task {jobId}");
                    
                    // Обновляем статус задачи
                    await DbJobManager.UpdateJobStatusAsync(_repository, jobId, ConversionStatus.Completed, 
                        mp3Url: existingItem.AudioUrl, newVideoUrl: existingItem.VideoUrl);
                    
                    return;
                }
                
                // Помещаем видео в очередь конвертации
                await _conversionChannel.Writer.WriteAsync((jobId, videoPath, videoHash));
                _logger.LogInformation($"Task {jobId} queued for conversion with video hash: {videoHash}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error downloading video for task {jobId}: {videoUrl}");
                
                // Обновляем статус задачи в случае ошибки
                await DbJobManager.UpdateJobStatusAsync(_repository, jobId, ConversionStatus.Failed, 
                    errorMessage: $"Error downloading video: {ex.Message}");
                
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
            
            try
            {
                // Извлекаем информацию о задаче из очереди
                var item = await _conversionChannel.Reader.ReadAsync();
                jobId = item.JobId;
                videoPath = item.VideoPath;
                videoHash = item.VideoHash;
                
                _logger.LogInformation($"Starting video conversion for task {jobId}: {videoPath}");
                
                // Обновляем статус
                await DbJobManager.UpdateJobStatusAsync(_repository, jobId, ConversionStatus.Converting);
                
                // Создаем выходной путь
                mp3Path = _tempFileManager.CreateTempFile(".mp3");
                
                // Обновляем задачу с путями к временным файлам
                var job = await _repository.GetJobByIdAsync(jobId);
                if (job != null)
                {
                    job.TempVideoPath = videoPath;
                    job.TempMp3Path = mp3Path;
                    await _repository.UpdateJobAsync(job);
                }
                
                // Получаем информацию о видео
                var mediaInfo = await FFmpeg.GetMediaInfo(videoPath);
                
                // Проверяем наличие аудиопотока
                if (mediaInfo.AudioStreams?.Count() == 0)
                {
                    throw new InvalidOperationException("No audio stream found in the video file");
                }
                
                // Конвертируем видео в MP3
                var conversion = FFmpeg.Conversions.New()
                    .AddStream(mediaInfo.AudioStreams)
                    .SetOutputFormat("mp3")
                    .SetAudioBitrate(128000)
                    .SetOutput(mp3Path);
                
                _logger.LogInformation($"Starting FFmpeg: {conversion.Build()}");
                
                // Запускаем и отслеживаем процесс конвертации
                await conversion.Start();
                
                _logger.LogInformation($"Conversion completed for task {jobId}");
                
                // Проверяем существование файла
                if (!File.Exists(mp3Path))
                {
                    throw new InvalidOperationException("Conversion completed but MP3 file not found");
                }
                
                // Помещаем в очередь загрузки MP3
                await _uploadChannel.Writer.WriteAsync((jobId, mp3Path, videoPath, videoHash));
                _logger.LogInformation($"Task {jobId} queued for MP3 upload");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error converting video for task {jobId}: {videoPath}");
                
                // Обновляем статус задачи в случае ошибки
                await DbJobManager.UpdateJobStatusAsync(_repository, jobId, ConversionStatus.Failed, 
                    errorMessage: $"Error converting video: {ex.Message}");
                
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
            
            try
            {
                // Извлекаем информацию о задаче из очереди
                var item = await _uploadChannel.Reader.ReadAsync();
                jobId = item.JobId;
                mp3Path = item.Mp3Path;
                videoHash = item.VideoHash;
                videoPath = item.VideoPath;
                
                _logger.LogInformation($"Starting MP3 upload for task {jobId}: {mp3Path}");
                
                // Обновляем статус
                await DbJobManager.UpdateJobStatusAsync(_repository, jobId, ConversionStatus.Uploading);
                
                // Получаем задачу
                var job = await _repository.GetJobByIdAsync(jobId);
                if (job == null)
                {
                    throw new InvalidOperationException($"Task {jobId} not found");
                }

                // Загружаем видео и MP3 в S3
                var videoUploadTask = _storageService.UploadFileAsync(videoPath, "video/mp4");
                var mp3UploadTask = _storageService.UploadFileAsync(mp3Path, "audio/mpeg");
                
                await Task.WhenAll(videoUploadTask, mp3UploadTask);
                
                var videoUrl = await videoUploadTask;
                var mp3Url = await mp3UploadTask;
                
                _logger.LogInformation($"Files uploaded for task {jobId}. Video URL: {videoUrl}, MP3 URL: {mp3Url}");
                
                var mediaItem = new MediaStorageItem
                {
                    VideoHash = videoHash,
                    VideoUrl = videoUrl,
                    AudioUrl = mp3Url,
                    FileSizeBytes = job.FileSizeBytes ?? 0
                };
                
                var savedItem = await _mediaItemRepository.SaveItemAsync(mediaItem);
                _logger.LogInformation($"Media item saved in C3 storage with ID: {savedItem.Id}");
                
                await DbJobManager.UpdateJobStatusAsync(_repository, jobId, ConversionStatus.Completed, mp3Url: mp3Url, newVideoUrl: videoUrl);
                
                _logger.LogInformation($"Task {jobId} completed successfully");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error uploading MP3 for task {jobId}: {mp3Path}");
                
                // Обновляем статус задачи в случае ошибки
                await DbJobManager.UpdateJobStatusAsync(_repository, jobId, ConversionStatus.Failed, 
                    errorMessage: $"Error uploading MP3: {ex.Message}");
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
                    _logger.LogWarning(ex, $"Error deleting temporary file: {path}");
                }
            }
        }
    }
}
