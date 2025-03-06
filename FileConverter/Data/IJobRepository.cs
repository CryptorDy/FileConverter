using FileConverter.Models;

namespace FileConverter.Data
{
    public interface IJobRepository
    {
        // Задачи
        Task<ConversionJob> CreateJobAsync(ConversionJob job);
        Task<ConversionJob?> GetJobByIdAsync(string jobId);
        Task<ConversionJob> UpdateJobAsync(ConversionJob job);
        Task<List<ConversionJob>> GetJobsByStatusAsync(ConversionStatus status, int take = 100);
        Task<List<ConversionJob>> GetAllJobsAsync(int skip = 0, int take = 20);
        
        // Пакетные задачи
        Task<BatchJob> CreateBatchJobAsync(BatchJob batchJob);
        Task<BatchJob?> GetBatchJobByIdAsync(string batchId);
        Task<List<ConversionJob>> GetJobsByBatchIdAsync(string batchId);
        Task<BatchJob> UpdateBatchJobAsync(BatchJob batchJob);
        
        // Специальные методы
        Task<ConversionJob> UpdateJobStatusAsync(string jobId, ConversionStatus status, string? mp3Url = null, string? errorMessage = null);
        Task<int> GetPendingJobsCountAsync();
        Task<bool> AnyJobsInProcessingAsync();
        
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
    }
} 