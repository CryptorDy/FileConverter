using Microsoft.Extensions.Caching.Memory;
using System.Security.Cryptography;
using System.Text;

namespace FileConverter.Services
{
    public class CacheManager
    {
        private readonly IMemoryCache _cache;
        private readonly ILogger<CacheManager> _logger;
        
        public CacheManager(IMemoryCache cache, ILogger<CacheManager> logger)
        {
            _cache = cache;
            _logger = logger;
        }
        
        /// <summary>
        /// Генерирует ключ кэша на основе URL видео
        /// </summary>
        public string GenerateCacheKey(string videoUrl)
        {
            using var sha = SHA256.Create();
            var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(videoUrl));
            return Convert.ToBase64String(hash).Replace('/', '_').Replace('+', '-').Replace('=', '~');
        }
        
        /// <summary>
        /// Проверяет наличие результата в кэше
        /// </summary>
        public bool TryGetMp3Url(string videoUrl, out string mp3Url)
        {
            var cacheKey = GenerateCacheKey(videoUrl);
            if (_cache.TryGetValue(cacheKey, out string? cachedMp3Url) && !string.IsNullOrEmpty(cachedMp3Url))
            {
                mp3Url = cachedMp3Url;
                _logger.LogInformation($"Found cached result for: {videoUrl}");
                return true;
            }
            
            mp3Url = string.Empty;
            return false;
        }
        
        /// <summary>
        /// Сохраняет результат конвертации в кэш
        /// </summary>
        public void CacheMp3Url(string videoUrl, string mp3Url, TimeSpan? expiration = null)
        {
            var cacheKey = GenerateCacheKey(videoUrl);
            var options = new MemoryCacheEntryOptions()
                .SetSize(1) // Размер записи для лимитирования памяти
                .SetPriority(CacheItemPriority.Normal)
                .SetAbsoluteExpiration(expiration ?? TimeSpan.FromDays(7));
                
            _cache.Set(cacheKey, mp3Url, options);
            _logger.LogInformation($"Cached conversion result for: {videoUrl}");
        }
        
        /// <summary>
        /// Удаляет запись из кэша
        /// </summary>
        public void InvalidateCache(string videoUrl)
        {
            var cacheKey = GenerateCacheKey(videoUrl);
            _cache.Remove(cacheKey);
            _logger.LogInformation($"Removed cached result for: {videoUrl}");
        }
    }
} 