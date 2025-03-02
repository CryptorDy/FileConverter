namespace FileConverter.Services
{
    public interface IFileConverterService
    {
        Task<List<string>> FromVideoToMP3Async(List<string> sourceUrls);
    }
} 