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
            // Настройки размеров очередей из конфигурации с разумными значениями по умолчанию
            int downloadQueueCapacity = configuration.GetValue<int>("Performance:DownloadQueueCapacity", 100);
            int conversionQueueCapacity = configuration.GetValue<int>("Performance:ConversionQueueCapacity", Math.Max(Environment.ProcessorCount, 4)); // Емкость зависит от процессора, но не меньше 4
            int keyframeExtractionQueueCapacity = configuration.GetValue<int>("Performance:KeyframeExtractionQueueCapacity", Math.Max(Environment.ProcessorCount, 4));
            int uploadQueueCapacity = configuration.GetValue<int>("Performance:UploadQueueCapacity", 10);
            int youtubeDownloadQueueCapacity = configuration.GetValue<int>("Performance:YoutubeDownloadQueueCapacity", 50);
            int audioAnalysisQueueCapacity = configuration.GetValue<int>("Performance:AudioAnalysisQueueCapacity", Math.Max(Environment.ProcessorCount, 4));

            DownloadChannel = Channel.CreateBounded<(string JobId, string VideoUrl)>(
                new BoundedChannelOptions(downloadQueueCapacity)
                {
                    FullMode = BoundedChannelFullMode.DropWrite, // Отбрасываем новые записи, если очередь полна (предотвращение deadlock)
                    SingleWriter = false, // Несколько писателей (API, RecoveryService)
                    SingleReader = false // Несколько читателей (DownloadBackgroundService воркеры)
                });

            ConversionChannel = Channel.CreateBounded<(string JobId, string VideoPath, string VideoHash)>(
                new BoundedChannelOptions(conversionQueueCapacity)
                {
                    FullMode = BoundedChannelFullMode.DropWrite, // Отбрасываем новые записи, если очередь полна
                    SingleWriter = false, // Несколько писателей (DownloadBackgroundService воркеры)
                    SingleReader = false // Несколько читателей (ConversionBackgroundService воркеры)
                });

            KeyframeExtractionChannel = Channel.CreateBounded<(string JobId, string VideoPath, string Mp3Path, string VideoHash)>(
                new BoundedChannelOptions(keyframeExtractionQueueCapacity)
                {
                    FullMode = BoundedChannelFullMode.DropWrite, // Отбрасываем новые записи, если очередь полна
                    SingleWriter = false, // Несколько писателей (ConversionBackgroundService воркеры)
                    SingleReader = false // Несколько читателей (KeyframeExtractionBackgroundService воркеры)
                });

            UploadChannel = Channel.CreateBounded<(string JobId, string Mp3Path, string VideoPath, string VideoHash, List<KeyframeInfo> KeyframeInfos)>(
                new BoundedChannelOptions(uploadQueueCapacity)
                {
                    FullMode = BoundedChannelFullMode.DropWrite, // Отбрасываем новые записи, если очередь полна
                    SingleWriter = false, // Несколько писателей (KeyframeExtractionBackgroundService воркеры)
                    SingleReader = false // Несколько читателей (UploadBackgroundService воркеры)
                });

            YoutubeDownloadChannel = Channel.CreateBounded<(string JobId, string VideoUrl)>(
                new BoundedChannelOptions(youtubeDownloadQueueCapacity)
                {
                    FullMode = BoundedChannelFullMode.DropWrite, // Отбрасываем новые записи, если очередь полна
                    SingleWriter = false, // Несколько писателей (API, RecoveryService)
                    SingleReader = false // Несколько читателей (YoutubeBackgroundService воркеры)
                });

            AudioAnalysisChannel = Channel.CreateBounded<(string JobId, string Mp3Path, string VideoPath, string VideoHash)>(
                new BoundedChannelOptions(audioAnalysisQueueCapacity)
                {
                    FullMode = BoundedChannelFullMode.DropWrite, // Отбрасываем новые записи, если очередь полна
                    SingleWriter = false, // Несколько писателей (ConversionBackgroundService воркеры)
                    SingleReader = false // Несколько читателей (AudioAnalysisBackgroundService воркеры)
                });
        }
    }
} 