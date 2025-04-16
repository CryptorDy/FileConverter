namespace FileConverter.Services.Interfaces
{
    public interface ITempFileManager
    {
        // Создает временную директорию и возвращает путь к ней
        string GetTempDirectory();

        // Создает временный файл с указанным расширением
        string CreateTempFile(string extension);

        // Создает временный файл с данными
        Task<string> CreateTempFileAsync(byte[] data, string extension);

        // Удаляет временный файл
        void DeleteTempFile(string? filePath);

        // Удаляет старые временные файлы
        Task CleanupOldTempFilesAsync(TimeSpan age);

        // Получает статистику по временным файлам
        Task<TempFileStats> GetTempFileStatsAsync();
    }

    public class TempFileStats
    {
        public int TotalFiles { get; set; }
        public long TotalSizeBytes { get; set; }
        public int OldFiles { get; set; } // Файлы старше 24 часов
        public long OldFilesSizeBytes { get; set; }
    }
}