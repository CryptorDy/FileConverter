using FileConverter.Services.Interfaces;
using Microsoft.Extensions.Logging;

namespace FileConverter.Services
{
    /// <summary>
    /// Унифицированный helper для очистки временных файлов во всех background сервисах
    /// </summary>
    public static class TempFileCleanupHelper
    {
        /// <summary>
        /// Безопасно удаляет временный файл с логированием
        /// </summary>
        public static void CleanupFile(ITempFileManager tempFileManager, string? filePath, ILogger logger, string jobId, string stage = "unknown")
        {
            if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
                return;

            try
            {
                tempFileManager.DeleteTempFile(filePath);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Задача {JobId}: Ошибка при удалении временного файла {Path} (этап {Stage})", jobId, filePath, stage);
            }
        }

        /// <summary>
        /// Безопасно удаляет список временных файлов с логированием
        /// </summary>
        public static void CleanupFiles(ITempFileManager tempFileManager, IEnumerable<string>? filePaths, ILogger logger, string jobId, string stage = "unknown")
        {
            if (filePaths == null) return;

            foreach (var filePath in filePaths.Where(p => !string.IsNullOrEmpty(p)))
            {
                CleanupFile(tempFileManager, filePath, logger, jobId, stage);
            }
        }

        /// <summary>
        /// Асинхронно удаляет временный файл с логированием через ConversionLogger
        /// </summary>
        public static async Task CleanupFileAsync(ITempFileManager tempFileManager, string? filePath, ILogger logger, 
            IConversionLogger conversionLogger, string jobId, string stage = "unknown")
        {
            if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
                return;

            try
            {
                tempFileManager.DeleteTempFile(filePath);
                await conversionLogger.LogSystemInfoAsync($"Временный файл удален ({stage}): {Path.GetFileName(filePath)}");
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Задача {JobId}: Ошибка при удалении временного файла {Path} (этап {Stage})", jobId, filePath, stage);
                await conversionLogger.LogWarningAsync(jobId, $"Ошибка при удалении временного файла ({stage}): {Path.GetFileName(filePath)}", ex.Message);
            }
        }

        /// <summary>
        /// Экстренная очистка всех связанных с задачей временных файлов при критических ошибках
        /// </summary>
        public static void EmergencyCleanup(ITempFileManager tempFileManager, ILogger logger, string jobId, 
            params string?[] filePaths)
        {
            logger.LogInformation("Задача {JobId}: Начинается экстренная очистка временных файлов", jobId);
            
            foreach (var filePath in filePaths.Where(p => !string.IsNullOrEmpty(p)))
            {
                CleanupFile(tempFileManager, filePath, logger, jobId, "emergency");
            }
            
            logger.LogInformation("Задача {JobId}: Экстренная очистка завершена", jobId);
        }
    }
} 