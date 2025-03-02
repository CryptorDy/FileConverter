using Microsoft.Extensions.Caching.Distributed;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace FileConverter.Services
{
    /// <summary>
    /// Менеджер распределенного кэша для масштабируемого решения
    /// </summary>
    public class DistributedCacheManager
    {
        private readonly IDistributedCache _cache;
        private readonly ILogger<DistributedCacheManager> _logger;
        private readonly DistributedCacheEntryOptions _defaultOptions;
        
        public DistributedCacheManager(
            IDistributedCache cache, 
            ILogger<DistributedCacheManager> logger,
            IConfiguration configuration)
        {
            _cache = cache;
            _logger = logger;
            
            // Настраиваем стандартные опции кэширования
            _defaultOptions = new DistributedCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = TimeSpan.FromDays(
                    configuration.GetValue<double>("Caching:DefaultExpirationDays", 7)),
                SlidingExpiration = TimeSpan.FromHours(
                    configuration.GetValue<double>("Caching:SlidingExpirationHours", 24))
            };
        }
        
        /// <summary>
        /// Генерирует ключ кэша на основе URL видео
        /// </summary>
        public string GenerateCacheKey(string videoUrl)
        {
            using var sha = SHA256.Create();
            var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(videoUrl));
            return "mp3:" + Convert.ToBase64String(hash).Replace('/', '_').Replace('+', '-').Replace('=', '~');
        }
        
        /// <summary>
        /// Проверяет наличие результата в кэше
        /// </summary>
        public async Task<(bool found, string mp3Url)> TryGetMp3UrlAsync(string videoUrl)
        {
            try
            {
                var cacheKey = GenerateCacheKey(videoUrl);
                var cachedValue = await _cache.GetStringAsync(cacheKey);
                
                if (string.IsNullOrEmpty(cachedValue))
                    return (false, string.Empty);
                
                _logger.LogInformation($"Найден кэшированный результат для: {videoUrl}");
                return (true, cachedValue);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, $"Ошибка при получении данных из кэша для {videoUrl}");
                return (false, string.Empty);
            }
        }
        
        /// <summary>
        /// Синхронная версия для совместимости с существующим кодом
        /// </summary>
        public bool TryGetMp3Url(string videoUrl, out string mp3Url)
        {
            var result = TryGetMp3UrlAsync(videoUrl).GetAwaiter().GetResult();
            mp3Url = result.mp3Url;
            return result.found;
        }
        
        /// <summary>
        /// Сохраняет результат конвертации в кэш
        /// </summary>
        public async Task CacheMp3UrlAsync(string videoUrl, string mp3Url, TimeSpan? expiration = null)
        {
            try
            {
                var cacheKey = GenerateCacheKey(videoUrl);
                
                var options = new DistributedCacheEntryOptions();
                if (expiration.HasValue)
                {
                    options.AbsoluteExpirationRelativeToNow = expiration;
                }
                else
                {
                    options = _defaultOptions;
                }
                
                await _cache.SetStringAsync(cacheKey, mp3Url, options);
                _logger.LogInformation($"Кэширован результат конвертации для: {videoUrl}");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, $"Ошибка при кэшировании результата для {videoUrl}");
            }
        }
        
        /// <summary>
        /// Синхронная версия для совместимости с существующим кодом
        /// </summary>
        public void CacheMp3Url(string videoUrl, string mp3Url, TimeSpan? expiration = null)
        {
            CacheMp3UrlAsync(videoUrl, mp3Url, expiration).GetAwaiter().GetResult();
        }
        
        /// <summary>
        /// Удаляет запись из кэша
        /// </summary>
        public async Task InvalidateCacheAsync(string videoUrl)
        {
            try
            {
                var cacheKey = GenerateCacheKey(videoUrl);
                await _cache.RemoveAsync(cacheKey);
                _logger.LogInformation($"Удален из кэша результат для: {videoUrl}");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, $"Ошибка при удалении из кэша результата для {videoUrl}");
            }
        }
        
        /// <summary>
        /// Кэширует другие данные (не mp3url)
        /// </summary>
        public async Task SetAsync<T>(string key, T value, TimeSpan? expiration = null)
        {
            try
            {
                var options = expiration.HasValue 
                    ? new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = expiration } 
                    : _defaultOptions;
                
                string jsonData = JsonSerializer.Serialize(value);
                await _cache.SetStringAsync(key, jsonData, options);
                _logger.LogDebug($"Данные кэшированы с ключом: {key}");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, $"Ошибка при кэшировании данных с ключом {key}");
            }
        }
        
        /// <summary>
        /// Получает данные из кэша
        /// </summary>
        public async Task<T?> GetAsync<T>(string key)
        {
            try
            {
                string? cachedData = await _cache.GetStringAsync(key);
                
                if (string.IsNullOrEmpty(cachedData))
                    return default;
                
                return JsonSerializer.Deserialize<T>(cachedData);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, $"Ошибка при получении данных из кэша с ключом {key}");
                return default;
            }
        }
    }
} 