using FileConverter.Data;
using FileConverter.Models;
using System.Collections.Concurrent;
using System.Threading.Channels;
using Xabe.FFmpeg;

namespace FileConverter.Services
{
    public class VideoProcessor : IVideoProcessor
    {
        private readonly IFileConverterService _fileConverterService;
        private readonly IS3StorageService _storageService;
        private readonly ILogger<VideoProcessor> _logger;
        private readonly CacheManager _cacheManager;
        private readonly ITempFileManager _tempFileManager;
        private readonly IJobRepository _repository;
        private readonly UrlValidator _urlValidator;
        
        // Очередь для загрузки видео с ограничением параллельных задач
        private static readonly Channel<(string JobId, string VideoUrl)> _downloadChannel = 
            Channel.CreateBounded<(string, string)>(new BoundedChannelOptions(100)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleWriter = false,
                SingleReader = false
            });
            
        // Очередь для конвертации видео
        private static readonly Channel<(string JobId, string VideoPath)> _conversionChannel = 
            Channel.CreateBounded<(string, string)>(new BoundedChannelOptions(Environment.ProcessorCount)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleWriter = false,
                SingleReader = false
            });
        
        // Очередь для загрузки MP3
        private static readonly Channel<(string JobId, string Mp3Path)> _uploadChannel = 
            Channel.CreateBounded<(string, string)>(new BoundedChannelOptions(10)
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

        public VideoProcessor(
            IFileConverterService fileConverterService,
            IS3StorageService storageService,
            ILogger<VideoProcessor> logger,
            CacheManager cacheManager,
            ITempFileManager tempFileManager,
            IJobRepository repository,
            UrlValidator urlValidator)
        {
            _fileConverterService = fileConverterService;
            _storageService = storageService;
            _logger = logger;
            _cacheManager = cacheManager;
            _tempFileManager = tempFileManager;
            _repository = repository;
            _urlValidator = urlValidator;
            
            // Инициализация обработчиков, если они еще не запущены
            StartWorkers();
        }
        
        // Метод для запуска рабочих потоков обработки
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
                    
                    _logger.LogInformation($"Запущено {downloadWorkers} обработчиков загрузки видео");
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
                    
                    _logger.LogInformation($"Запущено {conversionWorkers} обработчиков конвертации видео");
                }
                
                // Запуск обработчиков загрузки MP3
                if (!_uploadWorkersStarted)
                {
                    _uploadWorkersStarted = true;
                    int uploadWorkers = 5; // Максимум 5 параллельных загрузок в хранилище
                    
                    for (int i = 0; i < uploadWorkers; i++)
                    {
                        Task.Factory.StartNew(UploadWorker, 
                            TaskCreationOptions.LongRunning);
                    }
                    
                    _logger.LogInformation($"Запущено {uploadWorkers} обработчиков загрузки MP3");
                }
            }
        }

        // Входная точка обработки - получает jobId и размещает видео в очередь загрузки
        public async Task ProcessVideo(string jobId)
        {
            try
            {
                // Находим задачу в базе данных
                var job = await _repository.GetJobByIdAsync(jobId);
                if (job == null)
                {
                    _logger.LogError($"Задача {jobId} не найдена");
                    return;
                }
                
                // Проверяем кэш
                if (_cacheManager.TryGetMp3Url(job.VideoUrl, out string cachedMp3Url))
                {
                    _logger.LogInformation($"Найден кэшированный результат для задачи {jobId}: {cachedMp3Url}");
                    await DbJobManager.UpdateJobStatusAsync(_repository, _cacheManager, jobId, ConversionStatus.Completed, mp3Url: cachedMp3Url);
                    return;
                }
                
                // Проверяем безопасность URL
                if (!_urlValidator.IsUrlValid(job.VideoUrl))
                {
                    _logger.LogWarning($"Обнаружен небезопасный URL для задачи {jobId}: {job.VideoUrl}");
                    await DbJobManager.UpdateJobStatusAsync(_repository, _cacheManager, jobId, ConversionStatus.Failed, 
                        errorMessage: "Обнаружен некорректный или небезопасный URL");
                    return;
                }
                
                // Проверяем размер файла
                if (!await _urlValidator.IsFileSizeValid(job.VideoUrl))
                {
                    _logger.LogWarning($"Файл превышает допустимый размер для задачи {jobId}: {job.VideoUrl}");
                    await DbJobManager.UpdateJobStatusAsync(_repository, _cacheManager, jobId, ConversionStatus.Failed, 
                        errorMessage: "Файл превышает максимально допустимый размер");
                    return;
                }
                
                // Помещаем видео в очередь загрузки
                await _downloadChannel.Writer.WriteAsync((jobId, job.VideoUrl));
                _logger.LogInformation($"Задача {jobId} поставлена в очередь на загрузку");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Ошибка постановки задачи {jobId} в очередь");
                // Обновляем статус задачи в случае ошибки
                await DbJobManager.UpdateJobStatusAsync(_repository, _cacheManager, jobId, ConversionStatus.Failed, 
                    errorMessage: $"Ошибка постановки в очередь: {ex.Message}");
            }
        }
        
        // Воркер для загрузки видео
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
                    
                    _logger.LogInformation($"Начало загрузки видео для задачи {jobId}: {videoUrl}");
                    
                    try
                    {
                        // Обновляем статус
                        await DbJobManager.UpdateJobStatusAsync(_repository, _cacheManager, jobId, ConversionStatus.Downloading);
                    }
                    catch (Exception statusEx)
                    {
                        _logger.LogError(statusEx, "Ошибка при обновлении статуса задачи {JobId} на Downloading. Продолжаем обработку.", jobId);
                    }
                    
                    // Проверяем тип контента
                    var contentTypeResult = await _urlValidator.IsContentTypeValid(videoUrl);
                    if (!contentTypeResult.isValid)
                    {
                        _logger.LogWarning($"Недопустимый тип контента для {videoUrl}: {contentTypeResult.contentType}");
                        throw new InvalidOperationException($"Недопустимый тип контента: {contentTypeResult.contentType}");
                    }
                    
                    // Получаем расширение файла
                    string extension = GetFileExtension(videoUrl, contentTypeResult.contentType);
                    videoPath = _tempFileManager.CreateTempFile(extension);
                    
                    using (var httpClient = new HttpClient())
                    {
                        httpClient.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/123.0.0.0 Safari/537.36");
                        
                        // Добавляем заголовки для обхода ограничений социальных сетей
                        httpClient.DefaultRequestHeaders.Add("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,image/apng,*/*;q=0.8");
                        httpClient.DefaultRequestHeaders.Add("Accept-Language", "en-US,en;q=0.9,ru;q=0.8");
                        httpClient.DefaultRequestHeaders.Add("Referer", "https://www.instagram.com/");
                        httpClient.DefaultRequestHeaders.Add("sec-ch-ua", "\"Google Chrome\";v=\"123\", \"Not:A-Brand\";v=\"8\", \"Chromium\";v=\"123\"");
                        httpClient.DefaultRequestHeaders.Add("sec-ch-ua-mobile", "?0");
                        httpClient.DefaultRequestHeaders.Add("sec-ch-ua-platform", "\"Windows\"");
                        
                        byte[] fileData;
                        
                        if (await _storageService.FileExistsAsync(videoUrl))
                        {
                            fileData = await _storageService.DownloadFileAsync(videoUrl);
                            _logger.LogInformation($"Видео загружено из хранилища: {videoUrl}");
                        }
                        else
                        {
                            try
                            {
                                using var request = new HttpRequestMessage(HttpMethod.Get, videoUrl);
                                using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
                                
                                // Обрабатываем различные коды ошибок
                                if (response.StatusCode == System.Net.HttpStatusCode.Forbidden)
                                {
                                    // Для Instagram и других социальных сетей, которые блокируют прямой доступ
                                    throw new InvalidOperationException($"Доступ запрещен (403 Forbidden). URL может требовать авторизации или не поддерживает прямую загрузку: {videoUrl}");
                                }
                                else if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                                {
                                    throw new InvalidOperationException($"Файл не найден (404 Not Found): {videoUrl}");
                                }
                                else if (!response.IsSuccessStatusCode)
                                {
                                    throw new InvalidOperationException($"Ошибка HTTP при загрузке: {(int)response.StatusCode} {response.ReasonPhrase}");
                                }
                                
                                fileData = await response.Content.ReadAsByteArrayAsync();
                                _logger.LogInformation($"Видео загружено по URL: {videoUrl}");
                            }
                            catch (HttpRequestException httpEx)
                            {
                                if (httpEx.Message.Contains("403"))
                                {
                                    throw new InvalidOperationException($"Доступ запрещен (403 Forbidden). URL требует авторизации: {videoUrl}", httpEx);
                                }
                                else
                                {
                                    throw new InvalidOperationException($"Ошибка HTTP при загрузке: {httpEx.Message}", httpEx);
                                }
                            }
                        }
                        
                        await File.WriteAllBytesAsync(videoPath, fileData);
                        
                        try
                        {
                            // Обновляем задачу с размером файла и типом контента
                            var job = await _repository.GetJobByIdAsync(jobId);
                            if (job != null)
                            {
                                job.FileSizeBytes = fileData.Length;
                                job.ContentType = contentTypeResult.contentType;
                                job.TempVideoPath = videoPath;
                                await _repository.UpdateJobAsync(job);
                            }
                        }
                        catch (Exception updateEx)
                        {
                            _logger.LogError(updateEx, "Ошибка при обновлении информации о файле для задачи {JobId}. Продолжаем обработку.", jobId);
                        }
                    }
                    
                    // Помещаем видео в очередь конвертации
                    await _conversionChannel.Writer.WriteAsync((jobId, videoPath));
                    _logger.LogInformation($"Задача {jobId} поставлена в очередь на конвертацию");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, $"Ошибка загрузки видео для задачи {jobId}: {videoUrl}");
                    
                    try
                    {
                        await DbJobManager.UpdateJobStatusAsync(_repository, _cacheManager, jobId, ConversionStatus.Failed, 
                            errorMessage: $"Ошибка загрузки видео: {ex.Message}");
                    }
                    catch (Exception updateEx)
                    {
                        _logger.LogError(updateEx, "Не удалось обновить статус задачи {JobId} на Failed после ошибки загрузки", jobId);
                    }
                    
                    // Очищаем временные файлы в случае ошибки
                    try 
                    {
                        if (!string.IsNullOrEmpty(videoPath) && File.Exists(videoPath))
                        {
                            File.Delete(videoPath);
                            _logger.LogInformation($"Удален временный файл после ошибки: {videoPath}");
                        }
                    }
                    catch (Exception cleanupEx)
                    {
                        _logger.LogError(cleanupEx, "Ошибка при удалении временного файла: {Path}", videoPath);
                    }
                }
            }
        }
        
        // Воркер для конвертации видео
        private async Task ConversionWorker()
        {
            while (await _conversionChannel.Reader.WaitToReadAsync())
            {
                string jobId = string.Empty;
                string videoPath = string.Empty;
                string mp3Path = string.Empty;
                
                try
                {
                    // Извлекаем информацию о задаче из очереди
                    var item = await _conversionChannel.Reader.ReadAsync();
                    jobId = item.JobId;
                    videoPath = item.VideoPath;
                    
                    _logger.LogInformation($"Начало конвертации видео для задачи {jobId}: {videoPath}");
                    
                    // Обновляем статус
                    await DbJobManager.UpdateJobStatusAsync(_repository, _cacheManager, jobId, ConversionStatus.Converting);
                    
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
                    
                    // Асинхронно конвертируем видео
                    await FFmpeg.Conversions.New()
                        .AddStream(mediaInfo.AudioStreams)
                        .SetOutputFormat("mp3")
                        .SetAudioBitrate(128000)
                        .SetOutput(mp3Path)
                        .Start();
                    
                    if (!File.Exists(mp3Path))
                    {
                        throw new InvalidOperationException($"Не удалось создать MP3 файл: {mp3Path}");
                    }
                    
                    // Помещаем MP3 в очередь загрузки
                    await _uploadChannel.Writer.WriteAsync((jobId, mp3Path));
                    _logger.LogInformation($"Задача {jobId} поставлена в очередь на сохранение MP3");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, $"Ошибка конвертации видео для задачи {jobId}: {videoPath}");
                    await DbJobManager.UpdateJobStatusAsync(_repository, _cacheManager, jobId, ConversionStatus.Failed, 
                        errorMessage: $"Ошибка конвертации видео: {ex.Message}");
                    
                    // Очищаем временные файлы
                    _tempFileManager.DeleteTempFile(videoPath);
                    _tempFileManager.DeleteTempFile(mp3Path);
                }
            }
        }
        
        // Воркер для загрузки MP3 в хранилище
        private async Task UploadWorker()
        {
            while (await _uploadChannel.Reader.WaitToReadAsync())
            {
                string jobId = string.Empty;
                string mp3Path = string.Empty;
                string videoPath = string.Empty;
                
                try
                {
                    // Извлекаем информацию о задаче из очереди
                    var item = await _uploadChannel.Reader.ReadAsync();
                    jobId = item.JobId;
                    mp3Path = item.Mp3Path;
                    
                    _logger.LogInformation($"Начало загрузки MP3 в хранилище для задачи {jobId}: {mp3Path}");
                    
                    // Обновляем статус
                    await DbJobManager.UpdateJobStatusAsync(_repository, _cacheManager, jobId, ConversionStatus.Uploading);
                    
                    // Получаем информацию о задаче для получения видео-пути
                    var job = await _repository.GetJobByIdAsync(jobId);
                    if (job != null)
                    {
                        videoPath = job.TempVideoPath ?? string.Empty;
                    }
                    
                    // Загружаем MP3 в хранилище
                    string mp3Url = await _storageService.UploadFileAsync(mp3Path, "audio/mpeg");
                    
                    // Обновляем статус задачи
                    await DbJobManager.UpdateJobStatusAsync(_repository, _cacheManager, jobId, ConversionStatus.Completed, mp3Url: mp3Url);
                    
                    _logger.LogInformation($"Задача {jobId} успешно завершена: {mp3Url}");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, $"Ошибка загрузки MP3 для задачи {jobId}: {mp3Path}");
                    await DbJobManager.UpdateJobStatusAsync(_repository, _cacheManager, jobId, ConversionStatus.Failed, 
                        errorMessage: $"Ошибка загрузки MP3: {ex.Message}");
                }
                finally
                {
                    // Очищаем временные файлы
                    _tempFileManager.DeleteTempFile(videoPath);
                    _tempFileManager.DeleteTempFile(mp3Path);
                }
            }
        }
        
        // Вспомогательный метод для получения расширения файла
        private string GetFileExtension(string url, string contentType)
        {
            // Пытаемся получить расширение из URL
            string extension = Path.GetExtension(new Uri(url).AbsolutePath);
            
            // Очищаем расширение от параметров URL
            if (!string.IsNullOrEmpty(extension))
            {
                // Обрезаем всё после первого вхождения '?' или '#'
                int queryIndex = extension.IndexOfAny(new[] { '?', '#' });
                if (queryIndex > 0)
                {
                    extension = extension.Substring(0, queryIndex);
                }
            }

            // Если расширение отсутствует или некорректное, используем тип контента
            if (string.IsNullOrEmpty(extension) || extension.Length <= 1)
            {
                extension = contentType switch
                {
                    "video/mp4" => ".mp4",
                    "video/webm" => ".webm",
                    "video/ogg" => ".ogg",
                    "video/quicktime" => ".mov",
                    "video/x-ms-wmv" => ".wmv",
                    "video/x-msvideo" => ".avi",
                    "video/x-flv" => ".flv",
                    "audio/mpeg" => ".mp3",
                    "audio/mp4" => ".m4a",
                    "audio/wav" => ".wav",
                    "audio/webm" => ".weba",
                    "audio/ogg" => ".ogg",
                    _ => ".mp4" // Стандартное расширение по умолчанию
                };
            }
            
            return extension;
        }
    }
} 