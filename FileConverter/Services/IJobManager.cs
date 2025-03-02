using FileConverter.Models;

namespace FileConverter.Services
{
    public interface IJobManager
    {
        Task<ConversionJobResponse> EnqueueConversionJob(string videoUrl);
        Task<List<ConversionJobResponse>> EnqueueBatchJobs(List<string> videoUrls);
        Task<JobStatusResponse> GetJobStatus(string jobId);
        Task<List<JobStatusResponse>> GetBatchStatus(string batchId);
        Task<List<JobStatusResponse>> GetAllJobs(int skip = 0, int take = 20);
    }
} 