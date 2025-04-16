using FileConverter.Models;

namespace FileConverter.Services.Interfaces
{
    public interface IJobManager
    {
        Task<ConversionJobResponse> EnqueueConversionJob(string videoUrl);
        Task<BatchJobResult> EnqueueBatchJobs(List<string> videoUrls);
        Task<JobStatusResponse> GetJobStatus(string jobId);
        Task<List<JobStatusResponse>> GetBatchStatus(string batchId);
        Task<List<JobStatusResponse>> GetAllJobs(int skip = 0, int take = 20);
        Task<ConversionJob?> GetJobDetails(string jobId);
    }

    // Новый класс для возврата результата создания пакета задач
    public class BatchJobResult
    {
        public string BatchId { get; set; } = string.Empty;
        public List<ConversionJobResponse> Jobs { get; set; } = new List<ConversionJobResponse>();
    }
}