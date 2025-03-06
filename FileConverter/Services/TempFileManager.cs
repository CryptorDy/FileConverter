using System.Security.Cryptography;

namespace FileConverter.Services
{
    public class TempFileManager : ITempFileManager
    {
        private readonly ILogger<TempFileManager> _logger;
        private readonly IConfiguration _configuration;
        private readonly string _baseTempPath;
        private readonly long _maxTotalSizeBytes;

        public TempFileManager(ILogger<TempFileManager> logger, IConfiguration configuration)
        {
            _logger = logger;
            _configuration = configuration;
            
            // Получаем базовый путь для временных файлов (по умолчанию в temp директории)
            string configPath = _configuration["FileConverter:TempDirectory"];
            
            // Проверяем, что путь из конфигурации не пустой
            if (!string.IsNullOrEmpty(configPath))
            {
                _baseTempPath = configPath;
                _logger.LogInformation("Используем путь для временных файлов из конфигурации: {Path}", _baseTempPath);
            }
            else
            {
                // Используем стандартную временную директорию
                _baseTempPath = Path.Combine(Path.GetTempPath(), "FileConverter", "Temp");
                _logger.LogInformation("Путь к временным файлам не указан в конфигурации, используем стандартный: {Path}", _baseTempPath);
            }
                
            // Максимальный размер директории временных файлов (по умолчанию 10 ГБ)
            _maxTotalSizeBytes = long.TryParse(_configuration["FileConverter:MaxTempSizeBytes"], out long maxSize) 
                ? maxSize 
                : 10L * 1024 * 1024 * 1024; // 10 ГБ
            
            try
            {
                // Создаем директорию, если она не существует
                if (!Directory.Exists(_baseTempPath))
                {
                    Directory.CreateDirectory(_baseTempPath);
                    _logger.LogInformation("Создана директория для временных файлов: {Path}", _baseTempPath);
                }
            }
            catch (Exception ex)
            {
                // Если не удалось создать указанную директорию, используем запасной вариант
                _logger.LogError(ex, "Не удалось создать директорию {Path} для временных файлов", _baseTempPath);
                
                // Используем другой путь в качестве запасного варианта
                _baseTempPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Temp");
                _logger.LogWarning("Используем запасной путь для временных файлов: {Path}", _baseTempPath);
                
                // Создаем запасную директорию
                Directory.CreateDirectory(_baseTempPath);
            }
            
            _logger.LogInformation("Инициализирован менеджер временных файлов. Директория: {Path}, максимальный размер: {Size:F2} ГБ", 
                _baseTempPath, _maxTotalSizeBytes / (1024 * 1024 * 1024.0));
        }

        public string GetTempDirectory()
        {
            // Создаем поддиректорию с датой, чтобы легче было чистить
            string directory = Path.Combine(_baseTempPath, DateTime.UtcNow.ToString("yyyy-MM-dd"));
            Directory.CreateDirectory(directory);
            return directory;
        }

        public string CreateTempFile(string extension)
        {
            // Очищаем расширение от неправильных символов
            extension = CleanExtension(extension);
            
            // Используем криптографически безопасный генератор для имени файла
            string randomFileName = Path.GetRandomFileName() + extension;
            string filePath = Path.Combine(GetTempDirectory(), randomFileName);
            
            return filePath;
        }

        public async Task<string> CreateTempFileAsync(byte[] data, string extension)
        {
            string filePath = CreateTempFile(extension);
            
            // Проверяем, достаточно ли места
            EnsureSpaceAvailable(data.Length);
            
            // Записываем данные в файл
            await File.WriteAllBytesAsync(filePath, data);
            
            _logger.LogDebug($"Создан временный файл: {filePath}, размер: {data.Length / 1024.0:F2} КБ");
            
            return filePath;
        }

        public void DeleteTempFile(string? filePath)
        {
            if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
            {
                return;
            }
            
            try
            {
                // Проверяем, находится ли файл в нашей директории
                if (IsInTempDirectory(filePath))
                {
                    File.Delete(filePath);
                    _logger.LogDebug($"Удален временный файл: {filePath}");
                }
                else
                {
                    _logger.LogWarning($"Попытка удалить файл вне директории временных файлов: {filePath}");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Ошибка при удалении временного файла: {filePath}");
            }
        }

        public async Task CleanupOldTempFilesAsync(TimeSpan age)
        {
            try
            {
                DateTime cutoffTime = DateTime.UtcNow.Subtract(age);
                int deletedCount = 0;
                long freedSpace = 0;
                
                // Проверяем все файлы в директории и поддиректориях
                foreach (string file in Directory.GetFiles(_baseTempPath, "*", SearchOption.AllDirectories))
                {
                    try
                    {
                        FileInfo fileInfo = new FileInfo(file);
                        
                        // Проверяем, старше ли файл указанного возраста
                        if (fileInfo.LastWriteTimeUtc < cutoffTime)
                        {
                            long fileSize = fileInfo.Length;
                            fileInfo.Delete();
                            
                            deletedCount++;
                            freedSpace += fileSize;
                            
                            _logger.LogDebug($"Удален старый временный файл: {file}, возраст: {(DateTime.UtcNow - fileInfo.LastWriteTimeUtc).TotalHours:F1} ч");
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, $"Не удалось удалить временный файл: {file}");
                    }
                }
                
                // Удаляем пустые директории
                foreach (string dir in Directory.GetDirectories(_baseTempPath, "*", SearchOption.AllDirectories))
                {
                    try
                    {
                        if (!Directory.EnumerateFileSystemEntries(dir).Any())
                        {
                            Directory.Delete(dir);
                            _logger.LogDebug($"Удалена пустая директория: {dir}");
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, $"Не удалось удалить директорию: {dir}");
                    }
                }
                
                if (deletedCount > 0)
                {
                    _logger.LogInformation($"Очистка временных файлов: удалено {deletedCount} файлов, освобождено {freedSpace / (1024.0 * 1024):F2} МБ");
                }
                
                // При необходимости, вызываем принудительную сборку мусора
                if (freedSpace > 100 * 1024 * 1024) // Если освободили более 100 МБ
                {
                    GC.Collect();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ошибка при очистке временных файлов");
            }
        }

        public async Task<TempFileStats> GetTempFileStatsAsync()
        {
            var stats = new TempFileStats();
            long totalSize = 0;
            int totalFiles = 0;
            int oldFiles = 0;
            long oldFilesSize = 0;
            DateTime cutoffTime = DateTime.UtcNow.AddHours(-24);

            try
            {
                if (Directory.Exists(_baseTempPath))
                {
                    foreach (string file in Directory.GetFiles(_baseTempPath, "*", SearchOption.AllDirectories))
                    {
                        FileInfo fileInfo = new FileInfo(file);
                        totalSize += fileInfo.Length;
                        totalFiles++;

                        if (fileInfo.LastWriteTimeUtc < cutoffTime)
                        {
                            oldFiles++;
                            oldFilesSize += fileInfo.Length;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ошибка при получении статистики временных файлов");
            }

            stats.TotalFiles = totalFiles;
            stats.TotalSizeBytes = totalSize;
            stats.OldFiles = oldFiles;
            stats.OldFilesSizeBytes = oldFilesSize;

            return stats;
        }

        // Вспомогательные методы
        private string CleanExtension(string extension)
        {
            // Очищаем расширение от неправильных символов
            if (string.IsNullOrWhiteSpace(extension))
            {
                return ".tmp";
            }
            
            // Добавляем точку, если отсутствует
            if (!extension.StartsWith("."))
            {
                extension = "." + extension;
            }
            
            // Ограничиваем длину
            if (extension.Length > 10)
            {
                extension = extension.Substring(0, 10);
            }
            
            // Удаляем недопустимые символы
            foreach (char invalidChar in Path.GetInvalidFileNameChars())
            {
                extension = extension.Replace(invalidChar.ToString(), "");
            }
            
            // Если после очистки осталось пустое расширение, используем .tmp
            if (string.IsNullOrWhiteSpace(extension) || extension == ".")
            {
                extension = ".tmp";
            }
            
            return extension;
        }

        private bool IsInTempDirectory(string filePath)
        {
            // Проверяем, находится ли файл в нашей директории
            string fullPath = Path.GetFullPath(filePath);
            string fullTempPath = Path.GetFullPath(_baseTempPath);
            
            return fullPath.StartsWith(fullTempPath, StringComparison.OrdinalIgnoreCase);
        }

        private void EnsureSpaceAvailable(long requiredBytes)
        {
            try
            {
                var stats = GetTempFileStatsAsync().GetAwaiter().GetResult();
                
                // Если общий размер плюс требуемые байты превышает лимит
                if (stats.TotalSizeBytes + requiredBytes > _maxTotalSizeBytes)
                {
                    _logger.LogWarning($"Превышен лимит размера временной директории: {stats.TotalSizeBytes / (1024.0 * 1024 * 1024):F2} ГБ + {requiredBytes / (1024.0 * 1024):F2} МБ > {_maxTotalSizeBytes / (1024.0 * 1024 * 1024):F2} ГБ");
                    
                    // Пытаемся освободить место, начиная со старых файлов
                    CleanupOldTempFilesAsync(TimeSpan.FromHours(24)).GetAwaiter().GetResult();
                    
                    // Проверяем снова
                    stats = GetTempFileStatsAsync().GetAwaiter().GetResult();
                    if (stats.TotalSizeBytes + requiredBytes > _maxTotalSizeBytes)
                    {
                        // Если все еще не хватает места, освобождаем более новые файлы
                        CleanupOldTempFilesAsync(TimeSpan.FromHours(1)).GetAwaiter().GetResult();
                        
                        // Последняя проверка
                        stats = GetTempFileStatsAsync().GetAwaiter().GetResult();
                        if (stats.TotalSizeBytes + requiredBytes > _maxTotalSizeBytes)
                        {
                            // Если все еще не хватает места, выдаем ошибку
                            throw new IOException($"Недостаточно места в директории временных файлов. Требуется {requiredBytes / (1024.0 * 1024):F2} МБ, доступно {(_maxTotalSizeBytes - stats.TotalSizeBytes) / (1024.0 * 1024):F2} МБ");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ошибка при проверке доступного места");
                // Продолжаем выполнение, чтобы не блокировать работу, даже если нет места
            }
        }
    }
} 