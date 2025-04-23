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
        /// Канал для задач на загрузку MP3 в хранилище.
        /// Содержит кортеж (JobId, Mp3Path, VideoPath, VideoHash).
        /// </summary>
        public Channel<(string JobId, string Mp3Path, string VideoPath, string VideoHash)> UploadChannel { get; }

        public ProcessingChannels(IConfiguration configuration)
        {
            // Настройки размеров очередей из конфигурации с разумными значениями по умолчанию
            int downloadQueueCapacity = configuration.GetValue<int>("Performance:DownloadQueueCapacity", 100);
            int conversionQueueCapacity = configuration.GetValue<int>("Performance:ConversionQueueCapacity", Math.Max(Environment.ProcessorCount, 4)); // Емкость зависит от процессора, но не меньше 4
            int uploadQueueCapacity = configuration.GetValue<int>("Performance:UploadQueueCapacity", 10);

            DownloadChannel = Channel.CreateBounded<(string JobId, string VideoUrl)>(
                new BoundedChannelOptions(downloadQueueCapacity)
                {
                    FullMode = BoundedChannelFullMode.Wait, // Ожидать, если очередь полна
                    SingleWriter = false, // Несколько писателей (API, RecoveryService)
                    SingleReader = false // Несколько читателей (DownloadBackgroundService воркеры)
                });

            ConversionChannel = Channel.CreateBounded<(string JobId, string VideoPath, string VideoHash)>(
                new BoundedChannelOptions(conversionQueueCapacity)
                {
                    FullMode = BoundedChannelFullMode.Wait,
                    SingleWriter = false, // Несколько писателей (DownloadBackgroundService воркеры)
                    SingleReader = false // Несколько читателей (ConversionBackgroundService воркеры)
                });

            UploadChannel = Channel.CreateBounded<(string JobId, string Mp3Path, string VideoPath, string VideoHash)>(
                new BoundedChannelOptions(uploadQueueCapacity)
                {
                    FullMode = BoundedChannelFullMode.Wait,
                    SingleWriter = false, // Несколько писателей (ConversionBackgroundService воркеры)
                    SingleReader = false // Несколько читателей (UploadBackgroundService воркеры)
                });
        }
    }
} 