using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using System.Text;

namespace FileConverter.Services
{
    /// <summary>
    /// Адаптер, который позволяет использовать IMemoryCache как IDistributedCache.
    /// Упрощает переход между локальным и распределенным кэшированием.
    /// </summary>
    public class MemoryCacheAdapter : IDistributedCache
    {
        private readonly IMemoryCache _memoryCache;
        private readonly ILogger<MemoryCacheAdapter> _logger;

        public MemoryCacheAdapter(IMemoryCache memoryCache, ILogger<MemoryCacheAdapter> logger)
        {
            _memoryCache = memoryCache;
            _logger = logger;
        }

        public byte[]? Get(string key)
        {
            try
            {
                if (_memoryCache.TryGetValue(key, out byte[] value))
                {
                    return value;
                }
                return null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ошибка при получении данных из кэша по ключу {Key}", key);
                return null;
            }
        }

        public Task<byte[]?> GetAsync(string key, CancellationToken token = default)
        {
            return Task.FromResult(Get(key));
        }

        public void Set(string key, byte[] value, DistributedCacheEntryOptions options)
        {
            try
            {
                var memoryCacheOptions = new MemoryCacheEntryOptions();

                if (options != null)
                {
                    if (options.AbsoluteExpiration.HasValue)
                    {
                        memoryCacheOptions.AbsoluteExpiration = options.AbsoluteExpiration;
                    }

                    if (options.AbsoluteExpirationRelativeToNow.HasValue)
                    {
                        memoryCacheOptions.AbsoluteExpirationRelativeToNow = options.AbsoluteExpirationRelativeToNow;
                    }

                    if (options.SlidingExpiration.HasValue)
                    {
                        memoryCacheOptions.SlidingExpiration = options.SlidingExpiration;
                    }
                }

                _memoryCache.Set(key, value, memoryCacheOptions);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ошибка при сохранении данных в кэш по ключу {Key}", key);
            }
        }

        public Task SetAsync(string key, byte[] value, DistributedCacheEntryOptions options, CancellationToken token = default)
        {
            Set(key, value, options);
            return Task.CompletedTask;
        }

        public void Refresh(string key)
        {
            // Для IMemoryCache обновление не требуется - кэш уже в памяти
            // Но мы имитируем поведение, получая значение и устанавливая его снова
            try
            {
                if (_memoryCache.TryGetValue(key, out byte[] value))
                {
                    // Просто обновляем доступ для sliding expiration
                    _memoryCache.Get(key);
                    _logger.LogDebug("Ключ {Key} обновлен в кэше", key);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ошибка при обновлении ключа {Key} в кэше", key);
            }
        }

        public Task RefreshAsync(string key, CancellationToken token = default)
        {
            Refresh(key);
            return Task.CompletedTask;
        }

        public void Remove(string key)
        {
            try
            {
                _memoryCache.Remove(key);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ошибка при удалении ключа {Key} из кэша", key);
            }
        }

        public Task RemoveAsync(string key, CancellationToken token = default)
        {
            Remove(key);
            return Task.CompletedTask;
        }

        /// <summary>
        /// Вспомогательный метод для получения строкового значения
        /// </summary>
        public string? GetString(string key)
        {
            var data = Get(key);
            return data != null ? Encoding.UTF8.GetString(data) : null;
        }

        /// <summary>
        /// Вспомогательный метод для сохранения строкового значения
        /// </summary>
        public void SetString(string key, string? value, DistributedCacheEntryOptions options)
        {
            Set(key, value != null ? Encoding.UTF8.GetBytes(value) : null, options);
        }
        
        /// <summary>
        /// Вспомогательный асинхронный метод для получения строкового значения
        /// </summary>
        public async Task<string?> GetStringAsync(string key, CancellationToken token = default)
        {
            var data = await GetAsync(key, token);
            return data != null ? Encoding.UTF8.GetString(data) : null;
        }

        /// <summary>
        /// Вспомогательный асинхронный метод для сохранения строкового значения
        /// </summary>
        public async Task SetStringAsync(string key, string? value, DistributedCacheEntryOptions options, CancellationToken token = default)
        {
            await SetAsync(key, value != null ? Encoding.UTF8.GetBytes(value) : null, options, token);
        }
    }
} 