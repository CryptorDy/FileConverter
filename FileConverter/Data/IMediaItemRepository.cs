using FileConverter.Models;

namespace FileConverter.Data
{
    /// <summary>
    /// Репозиторий для работы с элементами медиахранилища
    /// </summary>
    public interface IMediaItemRepository
    {
        /// <summary>
        /// Находит элемент по хешу видео
        /// </summary>
        /// <param name="videoHash">Хеш видео</param>
        /// <returns>Элемент хранилища или null</returns>
        Task<MediaStorageItem?> FindByVideoHashAsync(string videoHash);

        /// <summary>
        /// Сохраняет новый элемент в хранилище
        /// </summary>
        /// <param name="item">Элемент для сохранения</param>
        /// <returns>Сохраненный элемент</returns>
        Task<MediaStorageItem> SaveItemAsync(MediaStorageItem item);

        /// <summary>
        /// Обновляет существующий элемент
        /// </summary>
        /// <param name="item">Элемент для обновления</param>
        /// <returns>Обновленный элемент</returns>
        Task<MediaStorageItem> UpdateItemAsync(MediaStorageItem item);

        /// <summary>
        /// Архивирует элемент вместо удаления
        /// </summary>
        /// <param name="id">Идентификатор элемента</param>
        /// <returns>Успешность операции</returns>
        Task<bool> ArchiveItemAsync(string id);
    }
} 