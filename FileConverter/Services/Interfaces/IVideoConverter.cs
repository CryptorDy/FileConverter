namespace FileConverter.Services
{
    /// <summary>
    /// Интерфейс для сервиса конвертации видео
    /// </summary>
    public interface IVideoConverter
    {
        /// <summary>
        /// Обрабатывает видео по указанному идентификатору задачи
        /// </summary>
        /// <param name="jobId">Идентификатор задачи конвертации</param>
        /// <returns>Задача, представляющая асинхронную операцию</returns>
        Task ProcessVideo(string jobId);
    }
} 