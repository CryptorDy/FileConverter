using FileConverter.Models;
using Microsoft.EntityFrameworkCore;

namespace FileConverter.Data
{
    /// <summary>
    /// Репозиторий для работы с элементами медиахранилища
    /// </summary>
    public class MediaItemRepository : IMediaItemRepository
    {
        private readonly DbContextFactory _dbContextFactory;
        private readonly ILogger<MediaItemRepository> _logger;

        public MediaItemRepository(
            DbContextFactory dbContextFactory,
            ILogger<MediaItemRepository> logger)
        {
            _dbContextFactory = dbContextFactory;
            _logger = logger;
        }

        /// <summary>
        /// Находит элемент по хешу видео
        /// </summary>
        public async Task<MediaStorageItem?> FindByVideoHashAsync(string videoHash)
        {
            try
            {
                return await _dbContextFactory.ExecuteWithDbContextAsync(async dbContext =>
                {
                    return await dbContext.MediaItems
                        .FirstOrDefaultAsync(m => m.VideoHash == videoHash);
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Ошибка при поиске элемента по хешу: {videoHash}");
                return null;
            }
        }

        /// <summary>
        /// Сохраняет новый элемент в хранилище
        /// </summary>
        public async Task<MediaStorageItem> SaveItemAsync(MediaStorageItem item)
        {
            try
            {
                if (string.IsNullOrEmpty(item.Id))
                {
                    item.Id = Guid.NewGuid().ToString();
                }

                return await _dbContextFactory.ExecuteWithDbContextAsync(async dbContext =>
                {
                    // Проверяем, существует ли элемент с таким же хешем в той же транзакции
                    var existingItem = await dbContext.MediaItems
                        .FirstOrDefaultAsync(m => m.VideoHash == item.VideoHash);
                    
                    if (existingItem != null)
                    {
                        // Если элемент существует, обновляем его
                        existingItem.LastAccessedAt = DateTime.UtcNow;
                        existingItem.VideoUrl = item.VideoUrl;
                        existingItem.AudioUrl = item.AudioUrl;
                        existingItem.Keyframes = item.Keyframes;
                        existingItem.FileSizeBytes = item.FileSizeBytes;
                        existingItem.DurationSeconds = item.DurationSeconds;
                        
                        await dbContext.SaveChangesAsync();
                        _logger.LogInformation($"Обновлен существующий элемент медиахранилища: {existingItem.Id}, VideoHash: {item.VideoHash}");
                        return existingItem;
                    }
                    else
                    {
                        // Если элемента нет, создаем новый
                        item.CreatedAt = DateTime.UtcNow;
                        dbContext.MediaItems.Add(item);
                        await dbContext.SaveChangesAsync();
                        
                        _logger.LogInformation($"Сохранен новый элемент медиахранилища: {item.Id}, VideoHash: {item.VideoHash}");
                        return item;
                    }
                });
            }
            catch (Microsoft.EntityFrameworkCore.DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException pgEx && pgEx.SqlState == "23505")
            {
                // Duplicate key - попробуем найти и вернуть существующий элемент
                _logger.LogWarning($"Элемент с VideoHash {item.VideoHash} уже существует. Возвращаем существующий элемент.");
                var existingItem = await FindByVideoHashAsync(item.VideoHash);
                if (existingItem != null)
                {
                    return existingItem;
                }
                
                // Если не найден, перебрасываем исключение
                _logger.LogError(ex, $"Ошибка при сохранении элемента: {item.VideoHash}");
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Ошибка при сохранении элемента: {item.VideoHash}");
                throw;
            }
        }

        /// <summary>
        /// Обновляет существующий элемент
        /// </summary>
        public async Task<MediaStorageItem> UpdateItemAsync(MediaStorageItem item)
        {
            try
            {
                return await _dbContextFactory.ExecuteWithDbContextAsync(async dbContext =>
                {
                    var existingItem = await dbContext.MediaItems
                        .FirstOrDefaultAsync(m => m.VideoHash == item.VideoHash);

                    if (existingItem == null)
                    {
                        // Создаем новый контекст для сохранения нового элемента
                        return await SaveItemAsync(item);
                    }

                    // Обновляем поля
                    existingItem.VideoUrl = item.VideoUrl;
                    existingItem.AudioUrl = item.AudioUrl;
                    existingItem.Keyframes = item.Keyframes;
                    existingItem.LastAccessedAt = DateTime.UtcNow;
                    existingItem.FileSizeBytes = item.FileSizeBytes;
                    existingItem.ContentType = item.ContentType;

                    dbContext.MediaItems.Update(existingItem);
                    await dbContext.SaveChangesAsync();
                    
                    _logger.LogInformation($"Обновлен элемент медиахранилища: {existingItem.Id}, VideoHash: {existingItem.VideoHash}");
                    return existingItem;
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Ошибка при обновлении элемента: {item.VideoHash}");
                throw;
            }
        }

        /// <summary>
        /// Архивирует элемент вместо удаления
        /// </summary>
        public async Task<bool> ArchiveItemAsync(string id)
        {
            try
            {
                return await _dbContextFactory.ExecuteWithDbContextAsync(async dbContext =>
                {
                    var item = await dbContext.MediaItems
                        .FirstOrDefaultAsync(m => m.Id == id);

                    if (item == null)
                    {
                        return false;
                    }

                    // Помечаем как архивированный (можно добавить поле IsArchived в модель)
                    item.LastAccessedAt = DateTime.UtcNow;
                    
                    // В данном случае мы не удаляем элемент, а только обновляем его
                    dbContext.MediaItems.Update(item);
                    await dbContext.SaveChangesAsync();
                    
                    _logger.LogInformation($"Архивирован элемент медиахранилища: {id}");
                    return true;
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Ошибка при архивировании элемента: {id}");
                return false;
            }
        }
    }
} 