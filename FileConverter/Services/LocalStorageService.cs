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
        private readonly IWebHostEnvironment _webHostEnvironment;
        private readonly IConfiguration _configuration;

        public LocalStorageService(
            ILogger<LocalStorageService> logger, 
            IConfiguration configuration,
            IWebHostEnvironment environment)
        {
            _logger = logger;
            _webHostEnvironment = environment;
            _configuration = configuration;

            // Определяем базовый путь для хранения файлов
            string storageBasePath;
            try
            {
                if (!string.IsNullOrEmpty(_webHostEnvironment.WebRootPath) && Directory.Exists(_webHostEnvironment.WebRootPath))
                {
                    storageBasePath = _webHostEnvironment.WebRootPath;
                    _logger.LogInformation("Using WebRootPath for storage: {Path}", storageBasePath);
                }
                else if (!string.IsNullOrEmpty(_webHostEnvironment.ContentRootPath))
                {
                    storageBasePath = Path.Combine(_webHostEnvironment.ContentRootPath, "wwwroot");
                    _logger.LogInformation("WebRootPath is empty or invalid, using ContentRootPath + wwwroot: {Path}", storageBasePath);
                }
                else
                {
                    // Абсолютный запасной вариант - используем текущую директорию приложения
                    storageBasePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "storage");
                    _logger.LogWarning("WebRootPath and ContentRootPath are empty, using fallback path: {Path}", storageBasePath);
                }
            }
            catch (Exception ex)
            {
                // В случае любых проблем с определением путей используем запасной вариант
                _logger.LogError(ex, "Error determining base storage path");
                storageBasePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "storage");
                _logger.LogWarning("Using fallback path due to error: {Path}", storageBasePath);
            }
            
            // Создаем базовую директорию, если она не существует
            try
            {
                if (!Directory.Exists(storageBasePath))
                {
                    Directory.CreateDirectory(storageBasePath);
                    _logger.LogInformation("Created base directory: {Path}", storageBasePath);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to create base directory: {Path}", storageBasePath);
                // Не выбрасываем исключение, продолжаем работу
            }
            
            // Устанавливаем путь для хранения MP3
            _storagePath = Path.Combine(storageBasePath, "mp3");
            _logger.LogInformation("Set MP3 storage path: {Path}", _storagePath);
            
            _baseUrl = configuration["AppSettings:BaseUrl"] ?? "https://localhost:7134";
            _contentTypeProvider = new FileExtensionContentTypeProvider();
            
            // Создаем директорию для MP3, если она не существует
            try
            {
                if (!Directory.Exists(_storagePath))
                {
                    Directory.CreateDirectory(_storagePath);
                    _logger.LogInformation("Created MP3 directory: {Path}", _storagePath);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to create MP3 directory: {Path}", _storagePath);
            }
        }

        public Task<bool> FileExistsAsync(string url)
        {
            try
            {
                if (Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) && 
                    (uri.Host.Contains("localhost") || uri.Host == "94.241.171.236"))
                {
                    string fileName = Path.GetFileName(uri.LocalPath);
                    string filePath = Path.Combine(_storagePath, fileName);
                    bool exists = File.Exists(filePath);
                    _logger.LogDebug("Checking file existence: {FilePath} - Exists: {Exists}", filePath, exists);
                    return Task.FromResult(exists);
                }
                
                _logger.LogWarning("Cannot check file existence for external URL: {Url}", url);
                return Task.FromResult(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error checking file existence: {Url}", url);
                return Task.FromResult(false);
            }
        }

        public Task<byte[]> DownloadFileAsync(string url)
        {
            try
            {
                if (Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) && 
                    (uri.Host.Contains("localhost") || uri.Host == "94.241.171.236"))
                {
                    string fileName = Path.GetFileName(uri.LocalPath);
                    if (string.IsNullOrEmpty(fileName))
                    {
                        _logger.LogError("Invalid file name in URL: {Url}", url);
                        throw new ArgumentException($"Invalid file name in URL: {url}");
                    }
                    
                    string filePath = Path.Combine(_storagePath, fileName);
                    _logger.LogDebug("Downloading file: {FilePath}", filePath);
                    
                    if (File.Exists(filePath))
                    {
                        return Task.FromResult(File.ReadAllBytes(filePath));
                    }
                    
                    _logger.LogWarning("File not found: {FilePath}", filePath);
                }
                else
                {
                    _logger.LogWarning("URL is not local: {Url}", url);
                }
                
                throw new FileNotFoundException($"File not found: {url}");
            }
            catch (FileNotFoundException)
            {
                // Пробрасываем исключение о ненайденном файле
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error downloading file: {Url}", url);
                throw new IOException($"Error downloading file: {url}", ex);
            }
        }

        public Task<string> UploadFileAsync(string filePath, string contentType)
        {
            try
            {
                if (string.IsNullOrEmpty(filePath))
                {
                    _logger.LogError("Empty file path during upload");
                    throw new ArgumentException("File path cannot be empty");
                }
                
                if (!File.Exists(filePath))
                {
                    _logger.LogError("File to upload does not exist: {FilePath}", filePath);
                    throw new FileNotFoundException($"File not found: {filePath}");
                }
                
                string fileName = Path.GetFileName(filePath);
                string uniqueFileName = $"{Path.GetFileNameWithoutExtension(fileName)}_{DateTime.Now.Ticks}{Path.GetExtension(fileName)}";
                string destinationPath = Path.Combine(_storagePath, uniqueFileName);
                
                _logger.LogDebug("Copying file from {Source} to {Destination}", filePath, destinationPath);
                File.Copy(filePath, destinationPath, true);
                
                // Формируем URL для доступа к файлу
                string fileUrl = $"{_baseUrl}/mp3/{HttpUtility.UrlEncode(uniqueFileName)}";
                
                _logger.LogInformation("File saved successfully: {DestinationPath}, URL: {FileUrl}", destinationPath, fileUrl);
                
                return Task.FromResult(fileUrl);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error uploading file: {FilePath}", filePath);
                throw;
            }
        }
        
        public Task<bool> DeleteFileAsync(string url)
        {
            try
            {
                if (Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) && 
                    (uri.Host.Contains("localhost") || uri.Host == "94.241.171.236"))
                {
                    string fileName = Path.GetFileName(uri.LocalPath);
                    string filePath = Path.Combine(_storagePath, fileName);
                    
                    if (File.Exists(filePath))
                    {
                        File.Delete(filePath);
                        _logger.LogInformation($"File deleted: {filePath}");
                        return Task.FromResult(true);
                    }
                    else
                    {
                        _logger.LogWarning($"File to delete not found: {filePath}");
                    }
                }
                else
                {
                    _logger.LogWarning($"Cannot delete file from external source: {url}");
                }
                
                return Task.FromResult(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error deleting file: {url}");
                return Task.FromResult(false);
            }
        }
        
        public Task<byte[]?> TryDownloadFileAsync(string url)
        {
            try
            {
                if (Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) && 
                    (uri.Host.Contains("localhost") || uri.Host == "94.241.171.236"))
                {
                    string fileName = Path.GetFileName(uri.LocalPath);
                    if (string.IsNullOrEmpty(fileName))
                    {
                        _logger.LogDebug("Invalid file name in URL: {Url}", url);
                        return Task.FromResult<byte[]?>(null);
                    }
                    
                    string filePath = Path.Combine(_storagePath, fileName);
                    _logger.LogDebug("Trying to download file: {FilePath}", filePath);
                    
                    if (File.Exists(filePath))
                    {
                        var data = File.ReadAllBytes(filePath);
                        return Task.FromResult<byte[]?>(data);
                    }
                    
                    _logger.LogDebug("File not found: {FilePath}", filePath);
                    return Task.FromResult<byte[]?>(null);
                }
                else
                {
                    _logger.LogDebug("URL is not local: {Url}", url);
                    return Task.FromResult<byte[]?>(null);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error downloading file: {Url}", url);
                return Task.FromResult<byte[]?>(null);
            }
        }
    }
} 