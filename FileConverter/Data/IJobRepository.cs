using FileConverter.Models;

namespace FileConverter.Data
{
    public interface IJobRepository
    {
        // Задачи
        Task<ConversionJob> CreateJobAsync(ConversionJob job);
        Task<ConversionJob?> GetJobByIdAsync(string jobId);
        
        /// <summary>
        /// Быстрое получение статуса задачи (с возможностью исключить тяжелые JSONB поля).
        /// </summary>
        Task<JobStatusResponse?> GetJobStatusResponseAsync(string jobId, bool includeDetails);
        Task<ConversionJob> UpdateJobAsync(ConversionJob job);
        Task<List<ConversionJob>> GetJobsByStatusAsync(ConversionStatus status, int take = 100);
        Task<List<ConversionJob>> GetAllJobsAsync(int skip = 0, int take = 20);
        
        // Пакетные задачи
        Task<BatchJob> CreateBatchJobAsync(BatchJob batchJob);
        Task<BatchJob?> GetBatchJobByIdAsync(string batchId);
        Task<List<ConversionJob>> GetJobsByBatchIdAsync(string batchId);
        
        /// <summary>
        /// Быстрое получение статусов всех задач в пакете (без тяжелых JSONB полей).
        /// </summary>
        Task<List<JobStatusResponse>> GetBatchStatusResponsesAsync(string batchId, bool includeDetails);
        Task<BatchJob> UpdateBatchJobAsync(BatchJob batchJob);
        
        // Специальные методы
        Task<ConversionJob> UpdateJobStatusAsync(
            string jobId, 
            ConversionStatus status, 
            string? mp3Url = null, 
            string? newVideoUrl = null,
            string? errorMessage = null);
        Task<int> GetPendingJobsCountAsync();
        Task<bool> AnyJobsInProcessingAsync();
        Task UpdateJobDurationAsync(string jobId, double durationSeconds);
        Task UpdateJobKeyframesAsync(string jobId, List<KeyframeInfo> keyframes);
        
        /// <summary>
        /// Получает завершенные задания с MP3 файлами, созданными ранее указанной даты
        /// </summary>
        /// <param name="date">Дата, ранее которой были созданы MP3 файлы</param>
        /// <returns>Список завершенных заданий с MP3 URL</returns>
        Task<IEnumerable<ConversionJob>> GetCompletedJobsWithMp3UrlOlderThanAsync(DateTime date);
        
        /// <summary>
        /// Получает список заданий, которые были в процессе, но не обновлялись дольше указанного времени
        /// </summary>
        Task<List<ConversionJob>> GetStaleJobsAsync(TimeSpan maxAge);
        
        /// <summary>
        /// Получает количество заданий с указанными статусами
        /// </summary>
        /// <param name="statuses">Список статусов для подсчета</param>
        /// <returns>Количество заданий</returns>
        Task<int> GetJobsByStatusesCountAsync(ConversionStatus[] statuses);
        
        /// <summary>
        /// Атомарно проверяет статус задачи и обновляет его если он соответствует ожидаемому
        /// </summary>
        /// <param name="jobId">ID задачи</param>
        /// <param name="expectedStatus">Ожидаемый статус для обновления</param>
        /// <param name="newStatus">Новый статус</param>
        /// <returns>true если статус был обновлен, false если статус не соответствует ожидаемому</returns>
        Task<bool> TryUpdateJobStatusIfAsync(string jobId, ConversionStatus expectedStatus, ConversionStatus newStatus);
        
        /// <summary>
        /// Обновляет данные анализа аудио для задачи
        /// </summary>
        Task UpdateJobAudioAnalysisAsync(string jobId, AudioAnalysisData audioAnalysis);
    }
} 