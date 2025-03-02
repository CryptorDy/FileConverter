namespace FileConverter.Services
{
    public interface IVideoProcessor
    {
        Task ProcessVideo(string jobId);
    }
} 