namespace FileConverter.Services.Interfaces
{
    /// <summary>
    /// Интерфейс для загрузки файлов в AssemblyAI
    /// </summary>
    public interface IAssemblyAIStorageService
    {
        /// <summary>
        /// Загружает файл в AssemblyAI
        /// </summary>
        /// <param name="filePath">Путь к файлу для загрузки</param>
        /// <param name="contentType">MIME-тип контента</param>
        /// <returns>upload_url от AssemblyAI</returns>
        Task<string> UploadFileAsync(string filePath, string contentType);
    }
}

