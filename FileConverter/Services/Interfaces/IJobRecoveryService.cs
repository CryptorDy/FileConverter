namespace FileConverter.Services.Interfaces
{
    /// <summary>
    /// Интерфейс сервиса восстановления "застрявших" заданий
    /// </summary>
    public interface IJobRecoveryService
    {
        /// <summary>
        /// Запускает процесс восстановления "застрявших" заданий
        /// </summary>
        /// <returns>Количество обработанных заданий</returns>
        Task<int> RecoverStaleJobsAsync();
        
        /// <summary>
        /// Настраивает расписание регулярного запуска восстановления заданий
        /// </summary>
        void ScheduleRecoveryJobs();
        
        /// <summary>
        /// Запускает процесс очистки устаревших логов
        /// </summary>
        /// <param name="days">Количество дней, после которых логи считаются устаревшими</param>
        /// <returns>Количество удаленных записей</returns>
        Task<int> CleanupOldLogsAsync(int days = 30);
        
        /// <summary>
        /// Собирает и возвращает статистику по очередям заданий
        /// </summary>
        /// <param name="hours">Период времени в часах</param>
        /// <returns>Статистика по очередям</returns>
        Task<QueueStatisticsResult> GetQueueStatisticsAsync(int hours = 24);
    }
    
    /// <summary>
    /// Результат статистики по очередям заданий
    /// </summary>
    public class QueueStatisticsResult
    {
        /// <summary>
        /// Успешно ли собрана статистика
        /// </summary>
        public bool Success { get; set; }
        
        /// <summary>
        /// Статистика по очередям
        /// </summary>
        public Data.QueueStatistics? Statistics { get; set; }
        
        /// <summary>
        /// Сообщение об ошибке, если статистика не собрана
        /// </summary>
        public string? ErrorMessage { get; set; }
        
        /// <summary>
        /// Время сбора статистики
        /// </summary>
        public DateTime CollectedAt { get; set; } = DateTime.UtcNow;
    }
} 