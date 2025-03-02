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
        Task<BatchJob> CreateBatchAsync(BatchJob batch);
        Task<BatchJob?> GetBatchByIdAsync(string batchId);
        Task<List<ConversionJob>> GetJobsByBatchIdAsync(string batchId);
        Task<BatchJob> UpdateBatchAsync(BatchJob batch);
        
        // Специальные методы
        Task<ConversionJob> UpdateJobStatusAsync(string jobId, ConversionStatus status, string? mp3Url = null, string? errorMessage = null);
        Task<bool> JobExistsWithVideoUrlAsync(string videoUrl);
        Task<ConversionJob?> GetCompletedJobByVideoUrlAsync(string videoUrl);
        
        // Метод для очистки MP3
        Task<List<ConversionJob>> GetCompletedJobsBeforeDateAsync(DateTime date);

        /// <summary>
        /// Получает список заданий в заданном статусе
        /// </summary>
        Task<IEnumerable<ConversionJob>> GetJobsByStatusAsync(ConversionStatus status);

        /// <summary>
        /// Получает завершенные задания с MP3 файлами, созданными ранее указанной даты
        /// </summary>
        /// <param name="date">Дата, ранее которой были созданы MP3 файлы</param>
        /// <returns>Список завершенных заданий с MP3 URL</returns>
        Task<IEnumerable<ConversionJob>> GetCompletedJobsWithMp3UrlOlderThanAsync(DateTime date);
    }
} 