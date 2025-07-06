using YoutubeExplode;
using YoutubeExplode.Videos.Streams;
using FileConverter.Services.Interfaces;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using System.IO;
using System.Threading.Tasks;
using System;
using System.Threading;

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

        public YoutubeDownloadService(
            ILogger<YoutubeDownloadService> logger,
            IConfiguration configuration,
            ITempFileManager tempFileManager)
        {
            _logger = logger;
            _configuration = configuration;
            _tempFileManager = tempFileManager;
            _youtubeClient = new YoutubeClient();
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
            try
            {
                _logger.LogInformation("Задача {JobId}: Начинаем скачивание YouTube видео: {VideoUrl}", jobId, videoUrl);

                // Получаем информацию о видео
                var video = await _youtubeClient.Videos.GetAsync(videoUrl, cancellationToken);
                _logger.LogInformation("Задача {JobId}: Получена информация о видео: {Title}", jobId, video.Title);

                // Получаем список доступных аудиопотоков
                var streamManifest = await _youtubeClient.Videos.Streams.GetManifestAsync(videoUrl, cancellationToken);
                
                // Выбираем лучший аудиопоток
                var audioStreamInfo = streamManifest.GetAudioOnlyStreams().GetWithHighestBitrate();
                
                if (audioStreamInfo == null)
                {
                    throw new InvalidOperationException($"Не удалось найти аудиопоток для видео: {videoUrl}");
                }

                _logger.LogInformation("Задача {JobId}: Выбран аудиопоток с битрейтом {Bitrate}", jobId, audioStreamInfo.Bitrate);

                // Создаем временный файл для MP3
                var mp3Path = _tempFileManager.CreateTempFile(".mp3");
                _logger.LogInformation("Задача {JobId}: Создан временный файл для MP3: {Mp3Path}", jobId, mp3Path);

                // Скачиваем аудиопоток напрямую в MP3 формате
                await _youtubeClient.Videos.Streams.DownloadAsync(audioStreamInfo, mp3Path, cancellationToken: cancellationToken);

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
            catch (Exception ex)
            {
                _logger.LogError(ex, "Задача {JobId}: Ошибка при скачивании YouTube видео {VideoUrl}", jobId, videoUrl);
                throw;
            }
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
                   lowerUrl.Contains("youtube.com/embed/");
        }
    }
} 