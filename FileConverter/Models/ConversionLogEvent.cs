using System.Text.Json.Serialization;

namespace FileConverter.Models
{
    /// <summary>
    /// Описывает событие логирования процесса конвертации
    /// </summary>
    public class ConversionLogEvent
    {
        /// <summary>
        /// Уникальный идентификатор события
        /// </summary>
        public string Id { get; set; } = Guid.NewGuid().ToString();

        /// <summary>
        /// Идентификатор задачи, к которой относится событие
        /// </summary>
        public string JobId { get; set; } = string.Empty;

        /// <summary>
        /// Идентификатор партии заданий (если есть)
        /// </summary>
        public string? BatchId { get; set; }

        /// <summary>
        /// Тип события логирования
        /// </summary>
        [JsonConverter(typeof(JsonStringEnumConverter))]
        public ConversionEventType EventType { get; set; }

        /// <summary>
        /// Статус задачи на момент события
        /// </summary>
        [JsonConverter(typeof(JsonStringEnumConverter))]
        public ConversionStatus JobStatus { get; set; }

        /// <summary>
        /// Дата и время события
        /// </summary>
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// Сообщение события
        /// </summary>
        public string Message { get; set; } = string.Empty;

        /// <summary>
        /// Детализированная информация о событии (если есть)
        /// </summary>
        public string? Details { get; set; }

        /// <summary>
        /// Информация об ошибке (если есть)
        /// </summary>
        public string? ErrorMessage { get; set; }

        /// <summary>
        /// Стек-трейс ошибки (если есть)
        /// </summary>
        public string? ErrorStackTrace { get; set; }

        /// <summary>
        /// URL видео (для релевантных событий)
        /// </summary>
        public string? VideoUrl { get; set; }

        /// <summary>
        /// URL MP3 файла (для релевантных событий)
        /// </summary>
        public string? Mp3Url { get; set; }

        /// <summary>
        /// Размер файла в байтах (для релевантных событий)
        /// </summary>
        public long? FileSizeBytes { get; set; }

        /// <summary>
        /// Длительность видео/аудио в секундах (для релевантных событий)
        /// </summary>
        public double? DurationSeconds { get; set; }

        /// <summary>
        /// Метрика скорости обработки (байт/сек) (для релевантных событий)
        /// </summary>
        public double? ProcessingRateBytesPerSecond { get; set; }

        /// <summary>
        /// Шаг обработки (для разделения на этапы внутри статуса)
        /// </summary>
        public int? Step { get; set; }

        /// <summary>
        /// Общее количество шагов (для разделения на этапы внутри статуса)
        /// </summary>
        public int? TotalSteps { get; set; }

        /// <summary>
        /// Номер попытки обработки
        /// </summary>
        public int AttemptNumber { get; set; } = 1;

        /// <summary>
        /// Время в очереди (для релевантных событий) в миллисекундах
        /// </summary>
        public long? QueueTimeMs { get; set; }

        /// <summary>
        /// Информация о причине блокировки/ожидания (если есть)
        /// </summary>
        public string? WaitReason { get; set; }
    }

    /// <summary>
    /// Типы событий логирования конвертации
    /// </summary>
    public enum ConversionEventType
    {
        /// <summary>
        /// Создание задачи
        /// </summary>
        JobCreated,

        /// <summary>
        /// Задача поставлена в очередь
        /// </summary>
        JobQueued,

        /// <summary>
        /// Изменение статуса задачи
        /// </summary>
        StatusChanged,

        /// <summary>
        /// Процесс загрузки видео начался
        /// </summary>
        DownloadStarted,

        /// <summary>
        /// Прогресс загрузки видео
        /// </summary>
        DownloadProgress,

        /// <summary>
        /// Загрузка видео завершена
        /// </summary>
        DownloadCompleted,

        /// <summary>
        /// Процесс конвертации начался
        /// </summary>
        ConversionStarted,

        /// <summary>
        /// Прогресс конвертации
        /// </summary>
        ConversionProgress,

        /// <summary>
        /// Конвертация завершена
        /// </summary>
        ConversionCompleted,

        /// <summary>
        /// Загрузка результата в хранилище начата
        /// </summary>
        UploadStarted,

        /// <summary>
        /// Прогресс загрузки результата
        /// </summary>
        UploadProgress,

        /// <summary>
        /// Загрузка результата завершена
        /// </summary>
        UploadCompleted,

        /// <summary>
        /// Задача завершена успешно
        /// </summary>
        JobCompleted,

        /// <summary>
        /// Произошла ошибка
        /// </summary>
        Error,

        /// <summary>
        /// Предупреждение
        /// </summary>
        Warning,

        /// <summary>
        /// Результат найден в кэше
        /// </summary>
        CacheHit,

        /// <summary>
        /// Задача восстановлена
        /// </summary>
        JobRecovered,

        /// <summary>
        /// Задача отменена
        /// </summary>
        JobCancelled,

        /// <summary>
        /// Задача задержана
        /// </summary>
        JobDelayed,

        /// <summary>
        /// Задача повторяется
        /// </summary>
        JobRetry,

        /// <summary>
        /// Системная информация
        /// </summary>
        SystemInfo
    }
}