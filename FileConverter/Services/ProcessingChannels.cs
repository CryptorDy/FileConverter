using System.Threading.Channels;
using FileConverter.Models;

namespace FileConverter.Services
{
    /// <summary>
    /// Хранит каналы для различных этапов обработки видео.
    /// Используется как Singleton для обеспечения доступа из разных сервисов.
    /// </summary>
    public class ProcessingChannels
    {
        /// <summary>
        /// Канал для задач на скачивание видео.
        /// Содержит кортеж (JobId, VideoUrl).
        /// </summary>
        public Channel<(string JobId, string VideoUrl)> DownloadChannel { get; }

        /// <summary>
        /// Канал для задач на конвертацию видео.
        /// Содержит кортеж (JobId, VideoPath, VideoHash).
        /// </summary>
        public Channel<(string JobId, string VideoPath, string VideoHash)> ConversionChannel { get; }

        /// <summary>
        /// Канал для задач на извлечение ключевых кадров.
        /// Содержит кортеж (JobId, VideoPath, Mp3Path, VideoHash).
        /// </summary>
        public Channel<(string JobId, string VideoPath, string Mp3Path, string VideoHash)> KeyframeExtractionChannel { get; }

        /// <summary>
        /// Канал для задач на загрузку файлов в хранилище.
        /// Содержит кортеж (JobId, Mp3Path, VideoPath, VideoHash, KeyframeInfos).
        /// </summary>
        public Channel<(string JobId, string Mp3Path, string VideoPath, string VideoHash, List<KeyframeInfo> KeyframeInfos)> UploadChannel { get; }

        /// <summary>
        /// Канал для задач на скачивание YouTube видео.
        /// Содержит кортеж (JobId, VideoUrl).
        /// </summary>
        public Channel<(string JobId, string VideoUrl)> YoutubeDownloadChannel { get; }

        /// <summary>
        /// Канал для задач на анализ аудио.
        /// Содержит кортеж (JobId, Mp3Path, VideoPath, VideoHash).
        /// </summary>
        public Channel<(string JobId, string Mp3Path, string VideoPath, string VideoHash)> AudioAnalysisChannel { get; }

        public ProcessingChannels(IConfiguration configuration)
        {
            // Все каналы делаем неограниченными, чтобы не отбрасывать задания
            DownloadChannel = Channel.CreateUnbounded<(string JobId, string VideoUrl)>(
                new UnboundedChannelOptions
                {
                    SingleWriter = false, // Несколько писателей (API, RecoveryService)
                    SingleReader = false // Несколько читателей (DownloadBackgroundService воркеры)
                });

            ConversionChannel = Channel.CreateUnbounded<(string JobId, string VideoPath, string VideoHash)>(
                new UnboundedChannelOptions
                {
                    SingleWriter = false, // Несколько писателей (DownloadBackgroundService воркеры)
                    SingleReader = false // Несколько читателей (ConversionBackgroundService воркеры)
                });

            // Для извлечения ключевых кадров используем неограниченный канал
            KeyframeExtractionChannel = Channel.CreateUnbounded<(string JobId, string VideoPath, string Mp3Path, string VideoHash)>(
                new UnboundedChannelOptions
                {
                    SingleWriter = false,
                    SingleReader = false
                });

            UploadChannel = Channel.CreateUnbounded<(string JobId, string Mp3Path, string VideoPath, string VideoHash, List<KeyframeInfo> KeyframeInfos)>(
                new UnboundedChannelOptions
                {
                    SingleWriter = false, // Несколько писателей (KeyframeExtractionBackgroundService воркеры)
                    SingleReader = false // Несколько читателей (UploadBackgroundService воркеры)
                });

            YoutubeDownloadChannel = Channel.CreateUnbounded<(string JobId, string VideoUrl)>(
                new UnboundedChannelOptions
                {
                    SingleWriter = false, // Несколько писателей (API, RecoveryService)
                    SingleReader = false // Несколько читателей (YoutubeBackgroundService воркеры)
                });

            // Для анализа аудио используем неограниченный канал, чтобы не отбрасывать задания
            AudioAnalysisChannel = Channel.CreateUnbounded<(string JobId, string Mp3Path, string VideoPath, string VideoHash)>(
                new UnboundedChannelOptions
                {
                    SingleWriter = false,
                    SingleReader = false
                });
        }
    }
} 