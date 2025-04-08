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
            
            // Обеспечиваем наличие пути хранения в любом случае
            string storageBasePath;
            
            try
            {
                // Порядок проверки путей от наиболее предпочтительного к запасным
                if (!string.IsNullOrEmpty(environment.WebRootPath))
                {
                    storageBasePath = environment.WebRootPath;
                    _logger.LogInformation("Используем WebRootPath: {Path}", storageBasePath);
                }
                else if (!string.IsNullOrEmpty(environment.ContentRootPath))
                {
                    storageBasePath = Path.Combine(environment.ContentRootPath, "wwwroot");
                    _logger.LogInformation("WebRootPath пуст, используем ContentRootPath + wwwroot: {Path}", storageBasePath);
                }
                else
                {
                    // Абсолютный запасной вариант - используем текущую директорию приложения
                    storageBasePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "storage");
                    _logger.LogWarning("WebRootPath и ContentRootPath пусты, используем запасной путь: {Path}", storageBasePath);
                }
            }
            catch (Exception ex)
            {
                // В случае любых проблем с определением путей используем запасной вариант
                _logger.LogError(ex, "Ошибка при определении базового пути хранения");
                storageBasePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "storage");
                _logger.LogWarning("Используем запасной путь из-за ошибки: {Path}", storageBasePath);
            }
            
            // Создаем базовую директорию, если она не существует
            try
            {
                if (!Directory.Exists(storageBasePath))
                {
                    Directory.CreateDirectory(storageBasePath);
                    _logger.LogInformation("Создана базовая директория: {Path}", storageBasePath);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Не удалось создать базовую директорию: {Path}", storageBasePath);
                // Не выбрасываем исключение, продолжаем работу
            }
            
            // Устанавливаем путь для хранения MP3
            _storagePath = Path.Combine(storageBasePath, "mp3");
            _logger.LogInformation("Установлен путь хранения MP3: {Path}", _storagePath);
            
            _baseUrl = configuration["AppSettings:BaseUrl"] ?? "https://localhost:7134";
            _contentTypeProvider = new FileExtensionContentTypeProvider();
            
            // Создаем директорию для MP3, если она не существует
            try
            {
                if (!Directory.Exists(_storagePath))
                {
                    Directory.CreateDirectory(_storagePath);
                    _logger.LogInformation("Создана директория для MP3: {Path}", _storagePath);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Не удалось создать директорию для MP3: {Path}", _storagePath);
                // Не выбрасываем исключение, продолжаем работу
            }
        }

        public Task<bool> FileExistsAsync(string url)
        {
            try
            {
                // Проверяем, является ли URL локальным файлом
                if (Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) && 
                    (uri.Host.Contains("localhost") || uri.Host == "94.241.171.236"))
                {
                    string fileName = Path.GetFileName(uri.LocalPath);
                    if (string.IsNullOrEmpty(fileName))
                    {
                        _logger.LogWarning("Недопустимое имя файла в URL: {Url}", url);
                        return Task.FromResult(false);
                    }
                    
                    string filePath = Path.Combine(_storagePath, fileName);
                    bool exists = File.Exists(filePath);
                    _logger.LogDebug("Проверка наличия файла: {FilePath}, Результат: {Exists}", filePath, exists);
                    return Task.FromResult(exists);
                }
                
                _logger.LogWarning("URL не является локальным: {Url}", url);
                return Task.FromResult(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ошибка при проверке существования файла: {Url}", url);
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
                        _logger.LogError("Недопустимое имя файла в URL: {Url}", url);
                        throw new ArgumentException($"Недопустимое имя файла в URL: {url}");
                    }
                    
                    string filePath = Path.Combine(_storagePath, fileName);
                    _logger.LogDebug("Загрузка файла: {FilePath}", filePath);
                    
                    if (File.Exists(filePath))
                    {
                        return Task.FromResult(File.ReadAllBytes(filePath));
                    }
                    
                    _logger.LogWarning("Файл не найден: {FilePath}", filePath);
                }
                else
                {
                    _logger.LogWarning("URL не является локальным: {Url}", url);
                }
                
                throw new FileNotFoundException($"Файл не найден: {url}");
            }
            catch (FileNotFoundException)
            {
                // Пробрасываем исключение о ненайденном файле
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ошибка при загрузке файла: {Url}", url);
                throw new IOException($"Ошибка при загрузке файла: {url}", ex);
            }
        }

        public Task<string> UploadFileAsync(string filePath, string contentType)
        {
            try
            {
                if (string.IsNullOrEmpty(filePath))
                {
                    _logger.LogError("Пустой путь к файлу при загрузке");
                    throw new ArgumentException("Путь к файлу не может быть пустым");
                }
                
                if (!File.Exists(filePath))
                {
                    _logger.LogError("Файл для загрузки не существует: {FilePath}", filePath);
                    throw new FileNotFoundException($"Файл не найден: {filePath}");
                }
                
                string fileName = Path.GetFileName(filePath);
                string uniqueFileName = $"{Path.GetFileNameWithoutExtension(fileName)}_{DateTime.Now.Ticks}{Path.GetExtension(fileName)}";
                string destinationPath = Path.Combine(_storagePath, uniqueFileName);
                
                _logger.LogDebug("Копирование файла из {Source} в {Destination}", filePath, destinationPath);
                File.Copy(filePath, destinationPath, true);
                
                // Формируем URL для доступа к файлу
                string fileUrl = $"{_baseUrl}/mp3/{HttpUtility.UrlEncode(uniqueFileName)}";
                
                _logger.LogInformation("Файл успешно сохранен: {DestinationPath}, URL: {FileUrl}", destinationPath, fileUrl);
                
                return Task.FromResult(fileUrl);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ошибка при загрузке файла: {FilePath}", filePath);
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