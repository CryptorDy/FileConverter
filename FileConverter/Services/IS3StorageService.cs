namespace FileConverter.Services
{
    public interface IS3StorageService
    {
        Task<bool> FileExistsAsync(string url);
        Task<byte[]> DownloadFileAsync(string url);
        Task<string> UploadFileAsync(string filePath, string contentType);
        Task<bool> DeleteFileAsync(string url);
    }
} 