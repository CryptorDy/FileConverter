namespace FileConverter.Services
{
    /// <summary>
    /// Интерфейс для работы с S3-совместимым хранилищем
    /// </summary>
    public interface IS3StorageService
    {
        /// <summary>
        /// Проверяет существование файла по URL
        /// </summary>
        /// <param name="url">URL файла</param>
        /// <returns>true, если файл существует</returns>
        Task<bool> FileExistsAsync(string url);

        /// <summary>
        /// Скачивает файл по URL
        /// </summary>
        /// <param name="url">URL файла</param>
        /// <returns>Содержимое файла в виде массива байтов</returns>
        Task<byte[]> DownloadFileAsync(string url);

        /// <summary>
        /// Загружает файл в хранилище
        /// </summary>
        /// <param name="filePath">Путь к файлу для загрузки</param>
        /// <param name="contentType">MIME-тип контента</param>
        /// <returns>URL загруженного файла</returns>
        Task<string> UploadFileAsync(string filePath, string contentType);

        /// <summary>
        /// Удаляет файл из хранилища
        /// </summary>
        /// <param name="url">URL файла для удаления</param>
        /// <returns>true, если файл успешно удален</returns>
        Task<bool> DeleteFileAsync(string url);
    }
} 