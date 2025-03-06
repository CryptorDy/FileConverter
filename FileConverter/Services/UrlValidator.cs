using System.Text.RegularExpressions;

namespace FileConverter.Services
{
    public class UrlValidator
    {
        private readonly ILogger<UrlValidator> _logger;
        private readonly IConfiguration _configuration;
        
        // Максимальный размер файла для скачивания (по умолчанию 500 МБ)
        private readonly long _maxFileSize;
        
        // Список разрешенных типов контента из конфигурации
        private readonly HashSet<string> _allowedContentTypes;

        public UrlValidator(ILogger<UrlValidator> logger, IConfiguration configuration)
        {
            _logger = logger;
            _configuration = configuration;
            
            // Загружаем максимальный размер файла из конфигурации (в байтах)
            _maxFileSize = long.TryParse(_configuration["FileConverter:MaxFileSizeBytes"], out long maxSize)
                ? maxSize
                : 500L * 1024 * 1024; // 500 МБ по умолчанию
            
            // Загружаем список разрешенных типов контента из конфигурации
            _allowedContentTypes = new HashSet<string>(
                _configuration.GetSection("FileConverter:AllowedFileTypes").Get<string[]>() ?? 
                new[] { "video/mp4", "video/webm", "audio/mpeg", "audio/mp4" }
            );
                
            _logger.LogInformation($"Инициализирован валидатор URL. " +
                                  $"Максимальный размер файла: {_maxFileSize / (1024.0 * 1024):F2} МБ, " +
                                  $"Разрешенные типы контента: {string.Join(", ", _allowedContentTypes)}");
        }

        /// <summary>
        /// Проверяет, является ли URL безопасным и допустимым
        /// </summary>
        public bool IsUrlValid(string url)
        {
            try
            {
                // Проверка на null или пустую строку
                if (string.IsNullOrWhiteSpace(url))
                {
                    _logger.LogWarning("Получен пустой URL");
                    return false;
                }
                
                // Проверка, что URL корректный
                if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) || 
                    (uri.Scheme != "http" && uri.Scheme != "https"))
                {
                    _logger.LogWarning($"Некорректный URL: {url}");
                    return false;
                }
                
                // Проверка на локальные адреса (localhost, 127.0.0.1 и т.д.)
                if (IsLocalHost(uri))
                {
                    _logger.LogWarning($"Запрещен доступ к локальным адресам: {url}");
                    return false;
                }
                
                // Проверка на потенциально опасные файлы
                if (IsPotentiallyDangerousFile(url))
                {
                    _logger.LogWarning($"Потенциально опасный тип файла: {url}");
                    return false;
                }
                
                // Особая обработка для социальных сетей
                if (IsSocialMediaUrl(uri))
                {
                    _logger.LogInformation($"Обнаружен URL социальной сети: {uri.Host}. Возможно потребуются особые заголовки для доступа.");
                    // Мы разрешаем URL социальных сетей, но предупреждаем о возможных проблемах
                }
                
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Ошибка при проверке URL: {url}");
                return false;
            }
        }
        
        /// <summary>
        /// Проверяет размер файла по URL (делает HEAD запрос)
        /// </summary>
        public async Task<bool> IsFileSizeValid(string url)
        {
            try
            {
                using var httpClient = new HttpClient();
                httpClient.DefaultRequestHeaders.Add("User-Agent", "FileConverter/1.0");
                
                // Сначала делаем HEAD запрос для проверки размера
                using var request = new HttpRequestMessage(HttpMethod.Head, url);
                using var response = await httpClient.SendAsync(request);
                
                // Получаем Content-Length, если есть
                if (response.Content.Headers.ContentLength.HasValue)
                {
                    long fileSize = response.Content.Headers.ContentLength.Value;
                    if (fileSize > _maxFileSize)
                    {
                        _logger.LogWarning($"Превышен максимальный размер файла: {url}, размер: {fileSize / (1024.0 * 1024):F2} МБ");
                        return false;
                    }
                    
                    _logger.LogInformation($"Размер файла для {url}: {fileSize / (1024.0 * 1024):F2} МБ");
                    return true;
                }
                
                // Если размер не указан в заголовках, мы не можем проверить размер
                _logger.LogWarning($"Невозможно определить размер файла для {url}");
                return true; // Считаем, что размер в порядке, если не можем его определить
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Ошибка при проверке размера файла: {url}");
                return false; // В случае ошибки считаем, что файл недопустим
            }
        }
        
        /// <summary>
        /// Проверяет MIME-тип файла
        /// </summary>
        public async Task<(bool isValid, string contentType)> IsContentTypeValid(string url)
        {
            try
            {
                using var httpClient = new HttpClient();
                httpClient.DefaultRequestHeaders.Add("User-Agent", "FileConverter/1.0");
                
                // Делаем HEAD запрос для проверки типа контента
                using var request = new HttpRequestMessage(HttpMethod.Head, url);
                using var response = await httpClient.SendAsync(request);
                
                string contentType = response.Content.Headers.ContentType?.MediaType ?? "";
                
                // Проверяем по списку разрешенных типов контента
                if (string.IsNullOrEmpty(contentType))
                {
                    _logger.LogWarning($"Пустой тип контента для {url}");
                    return (false, "empty");
                }
                
                // Специальная обработка для text/plain - это может быть неправильный тип от некоторых серверов
                if (contentType == "text/plain")
                {
                    // Проверим расширение файла в URL
                    Uri uri = new Uri(url);
                    string filename = Path.GetFileName(uri.LocalPath);
                    string extension = Path.GetExtension(filename).ToLowerInvariant();
                    
                    // Если у файла расширение видео или аудио, разрешаем его несмотря на тип контента
                    if (!string.IsNullOrEmpty(extension) && 
                        (extension == ".mp4" || extension == ".mov" || extension == ".mp3" || 
                         extension == ".avi" || extension == ".webm" || extension == ".ogg"))
                    {
                        _logger.LogWarning($"Получен text/plain для {url}, но у файла расширение {extension}. Разрешаем загрузку.");
                        return (true, $"video/{extension.TrimStart('.')}");
                    }
                }
                
                if (!_allowedContentTypes.Contains(contentType))
                {
                    _logger.LogWarning($"Недопустимый тип контента для {url}: {contentType}");
                    return (false, contentType);
                }
                
                _logger.LogInformation($"Тип контента для {url}: {contentType}");
                return (true, contentType);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Ошибка при проверке типа контента: {url}");
                return (false, "error");
            }
        }
        
        // Вспомогательные методы
        private bool IsLocalHost(Uri uri)
        {
            string host = uri.Host.ToLower();
            return host == "localhost" || 
                   host == "127.0.0.1" || 
                   host == "::1" ||
                   host.EndsWith(".local") ||
                   host.EndsWith(".internal");
        }
        
        private bool IsIpAddress(string host)
        {
            // Проверка IPv4
            if (System.Net.IPAddress.TryParse(host, out _))
            {
                return true;
            }
            
            // Проверка на частные IPv4 диапазоны в формате доменов
            string[] parts = host.Split('.');
            if (parts.Length == 4 && parts.All(p => int.TryParse(p, out _)))
            {
                return true;
            }
            
            return false;
        }
        
        private bool IsPotentiallyDangerousFile(string url)
        {
            // Проверяем расширение файла, если оно есть
            string extension = Path.GetExtension(new Uri(url).AbsolutePath).ToLower();
            
            // Список потенциально опасных расширений
            string[] dangerousExtensions = { 
                ".exe", ".dll", ".bat", ".cmd", ".sh", ".ps1", ".vbs", ".wsf", ".reg", 
                ".hta", ".pif", ".scr", ".inf", ".msi", ".com", ".js", ".jse" 
            };
            
            return dangerousExtensions.Contains(extension);
        }
        
        /// <summary>
        /// Определяет, принадлежит ли URL социальной сети или видеохостингу
        /// </summary>
        private bool IsSocialMediaUrl(Uri uri)
        {
            string host = uri.Host.ToLowerInvariant();
            
            // Список доменов социальных сетей и видеохостингов
            string[] socialMediaDomains = new[]
            {
                "instagram.com", "fbcdn.net", "facebook.com", 
                "youtube.com", "youtu.be", "vimeo.com",
                "tiktok.com", "twitter.com", "twimg.com",
                "pinterest.com", "snapchat.com", "linkedin.com"
            };
            
            return socialMediaDomains.Any(domain => host.Contains(domain));
        }
    }
} 