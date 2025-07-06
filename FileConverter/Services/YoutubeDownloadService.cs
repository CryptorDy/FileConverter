using YoutubeExplode;
using YoutubeExplode.Videos.Streams;
using YoutubeExplode.Exceptions;
using FileConverter.Services.Interfaces;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using System.IO;
using System.Threading.Tasks;
using System;
using System.Threading;
using System.Net.Http;
using System.Net;
using System.Linq;

namespace FileConverter.Services
{
    /// <summary>
    /// Сервис для скачивания YouTube видео и конвертации в MP3
    /// </summary>
    public class YoutubeDownloadService : IYoutubeDownloadService
    {
        private readonly ILogger<YoutubeDownloadService> _logger;
        private readonly IConfiguration _configuration;
        private readonly ITempFileManager _tempFileManager;
        private readonly YoutubeClient _youtubeClient;
        private readonly HttpClient _httpClient;
        private readonly IHttpClientFactory _httpClientFactory;
        
        // Настройки для обхода блокировок YouTube
        private readonly int _maxRetryAttempts;
        private readonly int _retryDelaySeconds;
        private readonly int _operationTimeoutSeconds;
        private readonly string[] _userAgents;

        public YoutubeDownloadService(
            ILogger<YoutubeDownloadService> logger,
            IConfiguration configuration,
            ITempFileManager tempFileManager,
            IHttpClientFactory httpClientFactory)
        {
            _logger = logger;
            _configuration = configuration;
            _tempFileManager = tempFileManager;
            _httpClientFactory = httpClientFactory;
            
            // Загружаем настройки из конфигурации
            _maxRetryAttempts = configuration.GetValue<int>("Youtube:MaxRetryAttempts", 3);
            _retryDelaySeconds = configuration.GetValue<int>("Youtube:RetryDelaySeconds", 5);
            _operationTimeoutSeconds = configuration.GetValue<int>("Youtube:OperationTimeoutSeconds", 180);
            
            // Список User-Agent для ротации
            _userAgents = new[]
            {
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/119.0.0.0 Safari/537.36",
                "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36",
                "Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36"
            };
            
            // Создаем HttpClient с прокси через фабрику
            _httpClient = _httpClientFactory.CreateClient("youtube-downloader");
            
            // Устанавливаем случайный User-Agent
            var randomUserAgent = _userAgents[new Random().Next(_userAgents.Length)];
            _httpClient.DefaultRequestHeaders.Clear();
            _httpClient.DefaultRequestHeaders.Add("User-Agent", randomUserAgent);
            _httpClient.DefaultRequestHeaders.Add("Accept-Language", "en-US,en;q=0.9");
            _httpClient.DefaultRequestHeaders.Add("Accept-Encoding", "gzip, deflate");
            
            // Устанавливаем таймаут
            _httpClient.Timeout = TimeSpan.FromSeconds(_operationTimeoutSeconds);
            
            _youtubeClient = new YoutubeClient(_httpClient);
            
            _logger.LogInformation("YoutubeDownloadService инициализирован с прокси. MaxRetries: {MaxRetries}, RetryDelay: {RetryDelay}с, Timeout: {Timeout}с", 
                _maxRetryAttempts, _retryDelaySeconds, _operationTimeoutSeconds);
        }

        /// <summary>
        /// Скачивает YouTube видео и конвертирует в MP3
        /// </summary>
        /// <param name="videoUrl">URL YouTube видео</param>
        /// <param name="jobId">ID задачи для логирования</param>
        /// <param name="cancellationToken">Токен отмены</param>
        /// <returns>Путь к созданному MP3 файлу</returns>
        public async Task<string> DownloadAndConvertToMp3Async(string videoUrl, string jobId, CancellationToken cancellationToken = default)
        {
            Exception? lastException = null;
            
            for (int attempt = 1; attempt <= _maxRetryAttempts; attempt++)
            {
                try
                {
                    _logger.LogInformation("Задача {JobId}: Попытка {Attempt}/{MaxAttempts} скачивания YouTube видео: {VideoUrl}", 
                        jobId, attempt, _maxRetryAttempts, videoUrl);

                    using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(_operationTimeoutSeconds));
                    using var combinedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
                    
                    // Получаем информацию о видео
                    _logger.LogDebug("Задача {JobId}: Получаем информацию о видео...", jobId);
                    var video = await _youtubeClient.Videos.GetAsync(videoUrl, combinedCts.Token);
                    _logger.LogInformation("Задача {JobId}: Получена информация о видео: {Title} (Длительность: {Duration})", 
                        jobId, video.Title, video.Duration);

                    // Получаем список доступных аудиопотоков
                    _logger.LogDebug("Задача {JobId}: Получаем список аудиопотоков...", jobId);
                    var streamManifest = await _youtubeClient.Videos.Streams.GetManifestAsync(videoUrl, combinedCts.Token);
                    
                    // Выбираем лучший аудиопоток
                    var audioStreamInfo = streamManifest.GetAudioOnlyStreams().GetWithHighestBitrate();
                    
                    if (audioStreamInfo == null)
                    {
                        // Попробуем найти любой аудиопоток
                        audioStreamInfo = streamManifest.GetAudioOnlyStreams().FirstOrDefault();
                        if (audioStreamInfo == null)
                        {
                            throw new InvalidOperationException($"Не удалось найти аудиопоток для видео: {videoUrl}");
                        }
                    }

                    _logger.LogInformation("Задача {JobId}: Выбран аудиопоток с битрейтом {Bitrate} ({Container})", 
                        jobId, audioStreamInfo.Bitrate, audioStreamInfo.Container);

                    // Создаем временный файл для MP3
                    var mp3Path = _tempFileManager.CreateTempFile(".mp3");
                    _logger.LogInformation("Задача {JobId}: Создан временный файл для MP3: {Mp3Path}", jobId, mp3Path);

                    // Скачиваем аудиопоток с прогрессом
                    _logger.LogDebug("Задача {JobId}: Начинаем скачивание аудиопотока...", jobId);
                    var progress = new Progress<double>(p => 
                    {
                        if (p % 0.1 < 0.01) // Логируем каждые 10%
                        {
                            _logger.LogDebug("Задача {JobId}: Прогресс скачивания: {Progress:P0}", jobId, p);
                        }
                    });
                    
                    await _youtubeClient.Videos.Streams.DownloadAsync(audioStreamInfo, mp3Path, progress, combinedCts.Token);

                    _logger.LogInformation("Задача {JobId}: YouTube видео успешно скачано и сконвертировано в MP3: {Mp3Path}", jobId, mp3Path);

                    // Проверяем, что файл создан и не пустой
                    var fileInfo = new FileInfo(mp3Path);
                    if (!fileInfo.Exists || fileInfo.Length == 0)
                    {
                        throw new InvalidOperationException($"Созданный MP3 файл пуст или не существует: {mp3Path}");
                    }

                    _logger.LogInformation("Задача {JobId}: Размер созданного MP3 файла: {FileSize} байт", jobId, fileInfo.Length);

                    return mp3Path;
                }
                catch (VideoUnavailableException ex)
                {
                    lastException = ex;
                    _logger.LogWarning("Задача {JobId}: Попытка {Attempt}/{MaxAttempts} - Видео недоступно: {Error}", 
                        jobId, attempt, _maxRetryAttempts, ex.Message);
                    
                    if (attempt == _maxRetryAttempts)
                        break;
                }
                catch (HttpRequestException ex) when (ex.Message.Contains("403") || ex.Message.Contains("Forbidden"))
                {
                    lastException = ex;
                    _logger.LogWarning("Задача {JobId}: Попытка {Attempt}/{MaxAttempts} - HTTP 403 Forbidden: {Error}", 
                        jobId, attempt, _maxRetryAttempts, ex.Message);
                    
                    if (attempt == _maxRetryAttempts)
                        break;
                }
                catch (TaskCanceledException ex) when (cancellationToken.IsCancellationRequested)
                {
                    _logger.LogInformation("Задача {JobId}: Операция отменена пользователем", jobId);
                    throw;
                }
                catch (TaskCanceledException ex)
                {
                    lastException = ex;
                    _logger.LogWarning("Задача {JobId}: Попытка {Attempt}/{MaxAttempts} - Превышен таймаут ({Timeout}с): {Error}", 
                        jobId, attempt, _maxRetryAttempts, _operationTimeoutSeconds, ex.Message);
                    
                    if (attempt == _maxRetryAttempts)
                        break;
                }
                catch (Exception ex)
                {
                    lastException = ex;
                    _logger.LogError(ex, "Задача {JobId}: Попытка {Attempt}/{MaxAttempts} - Неожиданная ошибка при скачивании YouTube видео", 
                        jobId, attempt, _maxRetryAttempts);
                    
                    if (attempt == _maxRetryAttempts)
                        break;
                }

                // Задержка перед следующей попыткой
                if (attempt < _maxRetryAttempts)
                {
                    var delaySeconds = _retryDelaySeconds * attempt; // Линейная задержка
                    _logger.LogInformation("Задача {JobId}: Ожидание {Delay}с перед следующей попыткой...", jobId, delaySeconds);
                    await Task.Delay(TimeSpan.FromSeconds(delaySeconds), cancellationToken);
                }
            }

            // Все попытки исчерпаны, формируем итоговую ошибку
            var finalErrorMessage = lastException switch
            {
                VideoUnavailableException ex => $"Видео YouTube недоступно: {ex.Message}. Возможные причины: видео удалено, заблокировано в регионе, требует авторизации или YouTube усилил защиту от ботов.",
                HttpRequestException ex when (ex.Message.Contains("403") || ex.Message.Contains("Forbidden")) => $"YouTube блокирует доступ (HTTP 403). Возможно требуется обновление библиотеки или использование прокси. Ошибка: {ex.Message}",
                TaskCanceledException ex => $"Превышен таймаут скачивания YouTube видео ({_operationTimeoutSeconds}с). Возможно медленное соединение или YouTube ограничивает скорость.",
                _ => $"Критическая ошибка при скачивании YouTube видео: {lastException?.Message}"
            };

            _logger.LogError("Задача {JobId}: Все {MaxAttempts} попыток исчерпаны. {ErrorMessage}", 
                jobId, _maxRetryAttempts, finalErrorMessage);
            
            throw new InvalidOperationException(finalErrorMessage, lastException);
        }

        /// <summary>
        /// Проверяет, является ли URL ссылкой на YouTube видео
        /// </summary>
        /// <param name="url">URL для проверки</param>
        /// <returns>true если это YouTube URL</returns>
        public bool IsYoutubeUrl(string url)
        {
            if (string.IsNullOrEmpty(url))
                return false;

            var lowerUrl = url.ToLowerInvariant();
            return lowerUrl.Contains("youtube.com/watch") || 
                   lowerUrl.Contains("youtu.be/") || 
                   lowerUrl.Contains("youtube.com/v/") ||
                   lowerUrl.Contains("youtube.com/embed/") ||
                   lowerUrl.Contains("youtube.com/shorts/") ||
                   lowerUrl.Contains("m.youtube.com/watch");
        }

        /// <summary>
        /// Освобождает ресурсы
        /// </summary>
        public void Dispose()
        {
            _httpClient?.Dispose();
            // YoutubeClient не реализует IDisposable в версии 6.5.4
        }
    }
} 