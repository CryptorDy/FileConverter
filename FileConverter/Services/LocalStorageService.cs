using Microsoft.AspNetCore.StaticFiles;
using System.Web;

namespace FileConverter.Services
{
    public class LocalStorageService : IS3StorageService
    {
        private readonly ILogger<LocalStorageService> _logger;
        private readonly string _storagePath;
        private readonly string _baseUrl;
        private readonly FileExtensionContentTypeProvider _contentTypeProvider;

        public LocalStorageService(
            ILogger<LocalStorageService> logger, 
            IConfiguration configuration,
            IWebHostEnvironment environment)
        {
            _logger = logger;
            _storagePath = Path.Combine(environment.WebRootPath, "mp3");
            _baseUrl = configuration["AppSettings:BaseUrl"] ?? "https://localhost:7134";
            _contentTypeProvider = new FileExtensionContentTypeProvider();
            
            // Создаем директорию, если она не существует
            if (!Directory.Exists(_storagePath))
            {
                Directory.CreateDirectory(_storagePath);
            }
        }

        public Task<bool> FileExistsAsync(string url)
        {
            // Проверяем, является ли URL локальным файлом
            if (Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) && 
                uri.Host.Contains("localhost"))
            {
                string fileName = Path.GetFileName(uri.LocalPath);
                string filePath = Path.Combine(_storagePath, fileName);
                return Task.FromResult(File.Exists(filePath));
            }
            
            return Task.FromResult(false);
        }

        public Task<byte[]> DownloadFileAsync(string url)
        {
            if (Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) && 
                uri.Host.Contains("localhost"))
            {
                string fileName = Path.GetFileName(uri.LocalPath);
                string filePath = Path.Combine(_storagePath, fileName);
                
                if (File.Exists(filePath))
                {
                    return Task.FromResult(File.ReadAllBytes(filePath));
                }
            }
            
            throw new FileNotFoundException($"Файл не найден: {url}");
        }

        public Task<string> UploadFileAsync(string filePath, string contentType)
        {
            try
            {
                string fileName = Path.GetFileName(filePath);
                string uniqueFileName = $"{Path.GetFileNameWithoutExtension(fileName)}_{DateTime.Now.Ticks}{Path.GetExtension(fileName)}";
                string destinationPath = Path.Combine(_storagePath, uniqueFileName);
                
                File.Copy(filePath, destinationPath, true);
                
                // Формируем URL для доступа к файлу
                string fileUrl = $"{_baseUrl}/mp3/{HttpUtility.UrlEncode(uniqueFileName)}";
                
                _logger.LogInformation($"Файл сохранен: {destinationPath}, URL: {fileUrl}");
                
                return Task.FromResult(fileUrl);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Ошибка при загрузке файла: {filePath}");
                throw;
            }
        }
        
        public Task<bool> DeleteFileAsync(string url)
        {
            try
            {
                if (Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) && 
                    uri.Host.Contains("localhost"))
                {
                    string fileName = Path.GetFileName(uri.LocalPath);
                    string filePath = Path.Combine(_storagePath, fileName);
                    
                    if (File.Exists(filePath))
                    {
                        File.Delete(filePath);
                        _logger.LogInformation($"Файл удален: {filePath}");
                        return Task.FromResult(true);
                    }
                    else
                    {
                        _logger.LogWarning($"Файл для удаления не найден: {filePath}");
                    }
                }
                else
                {
                    _logger.LogWarning($"Невозможно удалить файл из внешнего источника: {url}");
                }
                
                return Task.FromResult(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Ошибка при удалении файла: {url}");
                return Task.FromResult(false);
            }
        }
    }
} 