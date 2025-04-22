using FileConverter.Models;

namespace FileConverter.Services.Interfaces
{
    /// <summary>
    /// Интерфейс сервиса логирования процесса конвертации
    /// </summary>
    public interface IConversionLogger
    {
        /// <summary>
        /// Логирует создание задачи конвертации
        /// </summary>
        /// <param name="jobId">Идентификатор задачи</param>
        /// <param name="videoUrl">URL видео</param>
        /// <param name="batchId">Идентификатор пакетной задачи (опционально)</param>
        Task LogJobCreatedAsync(string jobId, string videoUrl, string? batchId = null);
        
        /// <summary>
        /// Логирует постановку задачи в очередь
        /// </summary>
        /// <param name="jobId">Идентификатор задачи</param>
        /// <param name="videoUrl">URL видео</param>
        /// <param name="details">Дополнительные детали</param>
        Task LogJobQueuedAsync(string jobId, string videoUrl, string? details = null);
        
        /// <summary>
        /// Логирует изменение статуса задачи
        /// </summary>
        /// <param name="jobId">Идентификатор задачи</param>
        /// <param name="status">Новый статус</param>
        /// <param name="details">Дополнительные детали</param>
        /// <param name="attemptNumber">Номер попытки (опционально)</param>
        Task LogStatusChangedAsync(string jobId, ConversionStatus status, string? details = null, int attemptNumber = 1);
        
        /// <summary>
        /// Логирует начало загрузки видео
        /// </summary>
        /// <param name="jobId">Идентификатор задачи</param>
        /// <param name="videoUrl">URL видео</param>
        /// <param name="queueTimeMs">Время ожидания в очереди (мс)</param>
        Task LogDownloadStartedAsync(string jobId, string videoUrl, long queueTimeMs);
        
        /// <summary>
        /// Логирует прогресс загрузки видео
        /// </summary>
        /// <param name="jobId">Идентификатор задачи</param>
        /// <param name="bytesReceived">Получено байт</param>
        /// <param name="totalBytes">Всего байт (опционально)</param>
        /// <param name="rateBytes">Скорость загрузки в байтах/сек (опционально)</param>
        Task LogDownloadProgressAsync(string jobId, long bytesReceived, long? totalBytes = null, double? rateBytes = null);
        
        /// <summary>
        /// Логирует завершение загрузки видео
        /// </summary>
        /// <param name="jobId">Идентификатор задачи</param>
        /// <param name="fileSizeBytes">Размер файла в байтах</param>
        /// <param name="path">Путь к файлу (опционально)</param>
        Task LogDownloadCompletedAsync(string jobId, long fileSizeBytes, string? path = null);
        
        /// <summary>
        /// Логирует начало конвертации
        /// </summary>
        /// <param name="jobId">Идентификатор задачи</param>
        /// <param name="queueTimeMs">Время ожидания в очереди (мс)</param>
        /// <param name="details">Дополнительные детали</param>
        Task LogConversionStartedAsync(string jobId, long queueTimeMs, string? details = null);
        
        /// <summary>
        /// Логирует прогресс конвертации
        /// </summary>
        /// <param name="jobId">Идентификатор задачи</param>
        /// <param name="percent">Процент выполнения</param>
        /// <param name="timeRemainingSeconds">Оставшееся время в секундах (опционально)</param>
        Task LogConversionProgressAsync(string jobId, double percent, double? timeRemainingSeconds = null);
        
        /// <summary>
        /// Логирует завершение конвертации
        /// </summary>
        /// <param name="jobId">Идентификатор задачи</param>
        /// <param name="fileSizeBytes">Размер файла в байтах</param>
        /// <param name="durationSeconds">Длительность в секундах</param>
        /// <param name="path">Путь к файлу (опционально)</param>
        Task LogConversionCompletedAsync(string jobId, long fileSizeBytes, double durationSeconds, string? path = null);
        
        /// <summary>
        /// Логирует начало загрузки результата в хранилище
        /// </summary>
        /// <param name="jobId">Идентификатор задачи</param>
        /// <param name="queueTimeMs">Время ожидания в очереди (мс)</param>
        /// <param name="fileSizeBytes">Размер файла в байтах</param>
        Task LogUploadStartedAsync(string jobId, long queueTimeMs, long fileSizeBytes);
        
        /// <summary>
        /// Логирует прогресс загрузки результата
        /// </summary>
        /// <param name="jobId">Идентификатор задачи</param>
        /// <param name="percent">Процент выполнения</param>
        /// <param name="bytesSent">Отправлено байт</param>
        Task LogUploadProgressAsync(string jobId, double percent, long bytesSent);
        
        /// <summary>
        /// Логирует завершение загрузки результата
        /// </summary>
        /// <param name="jobId">Идентификатор задачи</param>
        /// <param name="mp3Url">URL MP3 файла</param>
        Task LogUploadCompletedAsync(string jobId, string mp3Url);
        
        /// <summary>
        /// Логирует успешное завершение задачи
        /// </summary>
        /// <param name="jobId">Идентификатор задачи</param>
        /// <param name="mp3Url">URL MP3 файла</param>
        /// <param name="totalTimeMs">Общее время выполнения в мс</param>
        Task LogJobCompletedAsync(string jobId, string mp3Url, long totalTimeMs);
        
        /// <summary>
        /// Логирует ошибку при выполнении задачи
        /// </summary>
        /// <param name="jobId">Идентификатор задачи</param>
        /// <param name="errorMessage">Сообщение об ошибке</param>
        /// <param name="stackTrace">Стек-трейс ошибки (опционально)</param>
        /// <param name="status">Текущий статус задачи</param>
        Task LogErrorAsync(string jobId, string errorMessage, string? stackTrace = null, ConversionStatus status = ConversionStatus.Failed);
        
        /// <summary>
        /// Логирует предупреждение
        /// </summary>
        /// <param name="jobId">Идентификатор задачи</param>
        /// <param name="message">Сообщение</param>
        /// <param name="details">Дополнительные детали (опционально)</param>
        Task LogWarningAsync(string jobId, string message, string? details = null);
        
        /// <summary>
        /// Логирует нахождение кэшированного результата
        /// </summary>
        /// <param name="jobId">Идентификатор задачи</param>
        /// <param name="mp3Url">URL MP3 файла</param>
        /// <param name="videoHash">Хеш видео</param>
        Task LogCacheHitAsync(string jobId, string mp3Url, string videoHash);
        
        /// <summary>
        /// Логирует восстановление зависшей задачи
        /// </summary>
        /// <param name="jobId">Идентификатор задачи</param>
        /// <param name="previousStatus">Предыдущий статус</param>
        /// <param name="newStatus">Новый статус</param>
        /// <param name="reason">Причина восстановления</param>
        Task LogJobRecoveredAsync(string jobId, ConversionStatus previousStatus, ConversionStatus newStatus, string reason);
        
        /// <summary>
        /// Логирует отмену задачи
        /// </summary>
        /// <param name="jobId">Идентификатор задачи</param>
        /// <param name="reason">Причина отмены</param>
        Task LogJobCancelledAsync(string jobId, string reason);
        
        /// <summary>
        /// Логирует задержку задачи
        /// </summary>
        /// <param name="jobId">Идентификатор задачи</param>
        /// <param name="reason">Причина задержки</param>
        /// <param name="delayMs">Время задержки в мс</param>
        Task LogJobDelayedAsync(string jobId, string reason, long delayMs);
        
        /// <summary>
        /// Логирует повторную попытку выполнения задачи
        /// </summary>
        /// <param name="jobId">Идентификатор задачи</param>
        /// <param name="attemptNumber">Номер попытки</param>
        /// <param name="reason">Причина повтора</param>
        Task LogJobRetryAsync(string jobId, int attemptNumber, string reason);
        
        /// <summary>
        /// Логирует системную информацию
        /// </summary>
        /// <param name="message">Сообщение</param>
        /// <param name="details">Дополнительные детали (опционально)</param>
        Task LogSystemInfoAsync(string message, string? details = null);
    }
} 