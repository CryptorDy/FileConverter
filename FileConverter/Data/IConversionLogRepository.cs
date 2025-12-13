using FileConverter.Models;

namespace FileConverter.Data
{
    /// <summary>
    /// Интерфейс репозитория для работы с логами конвертации
    /// </summary>
    public interface IConversionLogRepository
    {
        /// <summary>
        /// Добавляет новую запись лога
        /// </summary>
        /// <param name="logEvent">Запись лога</param>
        /// <returns>Сохраненная запись лога</returns>
        Task<ConversionLogEvent> AddLogAsync(ConversionLogEvent logEvent);
        
        /// <summary>
        /// Добавляет пачку записей лога за одну транзакцию (для батчинга)
        /// </summary>
        /// <param name="logEvents">Список записей лога</param>
        Task CreateLogBatchAsync(List<ConversionLogEvent> logEvents);
        
        /// <summary>
        /// Получает все логи для конкретной задачи
        /// </summary>
        /// <param name="jobId">Идентификатор задачи</param>
        /// <returns>Список записей лога</returns>
        Task<List<ConversionLogEvent>> GetLogsByJobIdAsync(string jobId);
        
        /// <summary>
        /// Получает последние N логов для всех задач
        /// </summary>
        /// <param name="count">Количество записей</param>
        /// <returns>Список последних записей лога</returns>
        Task<List<ConversionLogEvent>> GetRecentLogsAsync(int count = 100);
        
        /// <summary>
        /// Получает все логи для партии задач
        /// </summary>
        /// <param name="batchId">Идентификатор партии</param>
        /// <returns>Список записей лога</returns>
        Task<List<ConversionLogEvent>> GetLogsByBatchIdAsync(string batchId);
        
        /// <summary>
        /// Получает логи по типу события за указанный период
        /// </summary>
        /// <param name="eventType">Тип события</param>
        /// <param name="startTime">Начало периода</param>
        /// <param name="endTime">Конец периода</param>
        /// <returns>Список записей лога</returns>
        Task<List<ConversionLogEvent>> GetLogsByEventTypeAsync(
            ConversionEventType eventType, 
            DateTime startTime, 
            DateTime endTime);
        
        /// <summary>
        /// Получает сводную статистику о работе очередей
        /// </summary>
        /// <param name="hours">Период в часах (по умолчанию 24 часа)</param>
        /// <returns>Статистика работы очередей</returns>
        Task<QueueStatistics> GetQueueStatisticsAsync(int hours = 24);
        
        /// <summary>
        /// Получает логи ошибок за указанный период
        /// </summary>
        /// <param name="startTime">Начало периода</param>
        /// <param name="endTime">Конец периода</param>
        /// <returns>Список записей лога ошибок</returns>
        Task<List<ConversionLogEvent>> GetErrorLogsAsync(
            DateTime startTime, 
            DateTime endTime);
        
        /// <summary>
        /// Получает последние логи для задач, которые застряли в процессе выполнения
        /// </summary>
        /// <param name="thresholdMinutes">Порог в минутах, после которого задача считается застрявшей</param>
        /// <returns>Логи для застрявших задач</returns>
        Task<List<ConversionLogEvent>> GetStaleJobLogsAsync(int thresholdMinutes = 30);
        
        /// <summary>
        /// Очищает старые логи
        /// </summary>
        /// <param name="thresholdDays">Порог в днях, старше которого логи будут удалены</param>
        /// <returns>Количество удаленных записей</returns>
        Task<int> PurgeOldLogsAsync(int thresholdDays = 30);
    }
    
    /// <summary>
    /// Статистика работы очередей
    /// </summary>
    public class QueueStatistics
    {
        /// <summary>
        /// Время, за которое собрана статистика (часы)
        /// </summary>
        public int TimeRangeHours { get; set; }
        
        /// <summary>
        /// Среднее время загрузки (мс)
        /// </summary>
        public double AverageDownloadTimeMs { get; set; }
        
        /// <summary>
        /// Среднее время конвертации (мс)
        /// </summary>
        public double AverageConversionTimeMs { get; set; }
        
        /// <summary>
        /// Среднее время загрузки результата (мс)
        /// </summary>
        public double AverageUploadTimeMs { get; set; }
        
        /// <summary>
        /// Среднее время ожидания в очереди загрузки (мс)
        /// </summary>
        public double AverageDownloadQueueTimeMs { get; set; }
        
        /// <summary>
        /// Среднее время ожидания в очереди конвертации (мс)
        /// </summary>
        public double AverageConversionQueueTimeMs { get; set; }
        
        /// <summary>
        /// Среднее время ожидания в очереди выгрузки (мс)
        /// </summary>
        public double AverageUploadQueueTimeMs { get; set; }
        
        /// <summary>
        /// Всего задач
        /// </summary>
        public int TotalJobs { get; set; }
        
        /// <summary>
        /// Успешно выполненных задач
        /// </summary>
        public int CompletedJobs { get; set; }
        
        /// <summary>
        /// Проваленных задач
        /// </summary>
        public int FailedJobs { get; set; }
        
        /// <summary>
        /// Текущая длина очереди загрузки
        /// </summary>
        public int CurrentDownloadQueueLength { get; set; }
        
        /// <summary>
        /// Текущая длина очереди конвертации
        /// </summary>
        public int CurrentConversionQueueLength { get; set; }
        
        /// <summary>
        /// Текущая длина очереди выгрузки
        /// </summary>
        public int CurrentUploadQueueLength { get; set; }
        
        /// <summary>
        /// Количество задач, застрявших в процессе
        /// </summary>
        public int StaleJobsCount { get; set; }
    }
} 