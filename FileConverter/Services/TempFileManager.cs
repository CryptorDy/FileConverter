using FileConverter.Services.Interfaces;
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
                _logger.LogInformation("Using temporary files path from configuration: {Path}", _baseTempPath);
            }
            else
            {
                // Используем стандартную временную директорию
                _baseTempPath = Path.Combine(Path.GetTempPath(), "FileConverter", "Temp");
                _logger.LogInformation("Temporary files path not specified in configuration, using default: {Path}", _baseTempPath);
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
                    _logger.LogInformation("Created temporary files directory: {Path}", _baseTempPath);
                }
            }
            catch (Exception ex)
            {
                // Если не удалось создать указанную директорию, используем запасной вариант
                _logger.LogError(ex, "Failed to create directory {Path} for temporary files", _baseTempPath);
                
                // Используем другой путь в качестве запасного варианта
                _baseTempPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Temp");
                _logger.LogWarning("Using fallback path for temporary files: {Path}", _baseTempPath);
                
                // Создаем запасную директорию
                Directory.CreateDirectory(_baseTempPath);
            }
            
            _logger.LogInformation("Initialized temporary file manager. Directory: {Path}, max size: {Size:F2} GB", 
                _baseTempPath, _maxTotalSizeBytes / (1024 * 1024 * 1024.0));
        }

        public string GetTempDirectory()
        {
            // Создаем поддиректорию с датой, чтобы легче было чистить
            string directory = Path.Combine(_baseTempPath, DateTime.UtcNow.ToString("yyyy-MM-dd"));
            Directory.CreateDirectory(directory);
            return directory;
        }

        public string CreateTempDirectory()
        {
            // Создаем уникальную поддиректорию с использованием GUID
            string uniqueDirName = Guid.NewGuid().ToString("N")[..8]; // Берем первые 8 символов GUID
            string directory = Path.Combine(GetTempDirectory(), uniqueDirName);
            Directory.CreateDirectory(directory);
            
            _logger.LogDebug("Created temporary directory: {Directory}", directory);
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
            
            _logger.LogDebug($"Created temporary file: {filePath}, size: {data.Length / 1024.0:F2} KB");
            
            return filePath;
        }

        public void DeleteTempFile(string? filePath)
        {
            if (string.IsNullOrEmpty(filePath))
            {
                _logger.LogWarning("Attempt to delete file with empty path");
                return;
            }

            if (!File.Exists(filePath))
            {
                _logger.LogWarning($"File does not exist: {filePath}");
                return;
            }
            
            try
            {
                // Check if the file is in our directory
                if (IsInTempDirectory(filePath))
                {
                    File.Delete(filePath);
                    _logger.LogInformation($"Temporary file deleted: {filePath}");
                }
                else
                {
                    _logger.LogWarning($"Attempt to delete file outside temporary directory: {filePath}. Base directory: {_baseTempPath}");
                }
            }
            catch (UnauthorizedAccessException ex)
            {
                _logger.LogError(ex, $"Access denied when deleting file: {filePath}. Check access rights.");
            }
            catch (IOException ex)
            {
                _logger.LogError(ex, $"IO error when deleting file: {filePath}. File may be used by another process.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Unknown error when deleting temporary file: {filePath}");
            }
        }

        public async Task CleanupOldTempFilesAsync(TimeSpan age)
        {
            try
            {
                DateTime cutoffTime = DateTime.UtcNow.Subtract(age);
                int deletedCount = 0;
                long freedSpace = 0;
                
                // Check all files in the directory and subdirectories
                foreach (string file in Directory.GetFiles(_baseTempPath, "*", SearchOption.AllDirectories))
                {
                    try
                    {
                        FileInfo fileInfo = new FileInfo(file);
                        
                        // Check if the file is older than the specified age
                        if (fileInfo.LastWriteTimeUtc < cutoffTime)
                        {
                            long fileSize = fileInfo.Length;
                            fileInfo.Delete();
                            
                            deletedCount++;
                            freedSpace += fileSize;
                            
                            _logger.LogDebug($"Deleted old temporary file: {file}, age: {(DateTime.UtcNow - fileInfo.LastWriteTimeUtc).TotalHours:F1} h");
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, $"Failed to delete temporary file: {file}");
                    }
                }
                
                // Delete empty directories
                foreach (string dir in Directory.GetDirectories(_baseTempPath, "*", SearchOption.AllDirectories))
                {
                    try
                    {
                        if (!Directory.EnumerateFileSystemEntries(dir).Any())
                        {
                            Directory.Delete(dir);
                            _logger.LogDebug($"Deleted empty directory: {dir}");
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, $"Failed to delete directory: {dir}");
                    }
                }
                
                if (deletedCount > 0)
                {
                    _logger.LogInformation($"Temporary files cleanup: deleted {deletedCount} files, freed {freedSpace / (1024.0 * 1024):F2} MB");
                }
                
                // Trigger garbage collection if necessary
                if (freedSpace > 100 * 1024 * 1024) // If more than 100 MB freed
                {
                    GC.Collect();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error while cleaning up temporary files");
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
                _logger.LogError(ex, "Error getting temporary files statistics");
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
            try
            {
                // Check if the file is in our directory
                string fullPath = Path.GetFullPath(filePath);
                string fullTempPath = Path.GetFullPath(_baseTempPath);
                
                // Нормализуем пути для сравнения
                fullPath = fullPath.Replace("\\", "/").TrimEnd('/');
                fullTempPath = fullTempPath.Replace("\\", "/").TrimEnd('/');
                
                bool isInTempDir = fullPath.StartsWith(fullTempPath, StringComparison.OrdinalIgnoreCase);
                
                if (!isInTempDir)
                {
                    _logger.LogWarning($"Attempt to access file outside of temporary directory. File: {fullPath}, Temp directory: {fullTempPath}");
                }
                
                return isInTempDir;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error checking if file belongs to temporary directory: {filePath}");
                return false;
            }
        }

        private void EnsureSpaceAvailable(long requiredBytes)
        {
            try
            {
                var stats = GetTempFileStatsAsync().GetAwaiter().GetResult();
                
                // If total size plus required bytes exceeds the limit
                if (stats.TotalSizeBytes + requiredBytes > _maxTotalSizeBytes)
                {
                    _logger.LogWarning($"Temporary directory size limit exceeded: {stats.TotalSizeBytes / (1024.0 * 1024 * 1024):F2} GB + {requiredBytes / (1024.0 * 1024):F2} MB > {_maxTotalSizeBytes / (1024.0 * 1024 * 1024):F2} GB");
                    
                    // Try to free up space, starting with old files
                    CleanupOldTempFilesAsync(TimeSpan.FromHours(24)).GetAwaiter().GetResult();
                    
                    // Check again
                    stats = GetTempFileStatsAsync().GetAwaiter().GetResult();
                    if (stats.TotalSizeBytes + requiredBytes > _maxTotalSizeBytes)
                    {
                        // If still not enough space, free up newer files
                        CleanupOldTempFilesAsync(TimeSpan.FromHours(1)).GetAwaiter().GetResult();
                        
                        // Last check
                        stats = GetTempFileStatsAsync().GetAwaiter().GetResult();
                        if (stats.TotalSizeBytes + requiredBytes > _maxTotalSizeBytes)
                        {
                            // If still not enough space, throw an error
                            throw new IOException($"Not enough space in the temporary files directory. Required {requiredBytes / (1024.0 * 1024):F2} MB, available {(_maxTotalSizeBytes - stats.TotalSizeBytes) / (1024.0 * 1024):F2} MB");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error checking available space");
                // Continue execution to avoid blocking work, even if there is no space
            }
        }
    }
} 