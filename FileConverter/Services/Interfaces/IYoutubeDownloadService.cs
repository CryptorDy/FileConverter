using System.Threading;
using System.Threading.Tasks;
using System;

namespace FileConverter.Services.Interfaces
{
    /// <summary>
    /// Интерфейс для сервиса скачивания YouTube видео
    /// </summary>
    public interface IYoutubeDownloadService : IDisposable
    {
        /// <summary>
        /// Скачивает YouTube видео и конвертирует в MP3
        /// </summary>
        /// <param name="videoUrl">URL YouTube видео</param>
        /// <param name="jobId">ID задачи для логирования</param>
        /// <param name="cancellationToken">Токен отмены</param>
        /// <returns>Путь к созданному MP3 файлу</returns>
        Task<string> DownloadAndConvertToMp3Async(string videoUrl, string jobId, CancellationToken cancellationToken = default);

        /// <summary>
        /// Проверяет, является ли URL ссылкой на YouTube видео
        /// </summary>
        /// <param name="url">URL для проверки</param>
        /// <returns>true если это YouTube URL</returns>
        bool IsYoutubeUrl(string url);
    }
} 