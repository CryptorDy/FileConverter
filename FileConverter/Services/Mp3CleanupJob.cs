using Hangfire;
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Collections.Generic;
using FileConverter.Data;
using FileConverter.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;

namespace FileConverter.Services
{
    /// <summary>
    /// Задание для очистки MP3 файлов старше 1 часа
    /// </summary>
    public class Mp3CleanupJob
    {
        private readonly ILogger<Mp3CleanupJob> _logger;
        private readonly IConfiguration _configuration;
        private readonly IJobRepository _jobRepository;
        private readonly IS3StorageService _storageService;

        public Mp3CleanupJob(
            ILogger<Mp3CleanupJob> logger,
            IConfiguration configuration,
            IJobRepository jobRepository,
            IS3StorageService storageService)
        {
            _logger = logger;
            _configuration = configuration;
            _jobRepository = jobRepository;
            _storageService = storageService;
        }

        /// <summary>
        /// Настраивает периодический запуск задания очистки MP3
        /// </summary>
        public static void ScheduleJobs()
        {
            // Запускаем задание каждые 10 минут
            RecurringJob.AddOrUpdate<Mp3CleanupJob>(
                "mp3-cleanup-job", 
                job => job.CleanupExpiredMp3Files(), 
                "*/10 * * * *");
        }

        /// <summary>
        /// Удаляет MP3 файлы, созданные более часа назад
        /// </summary>
        public async Task CleanupExpiredMp3Files()
        {
            _logger.LogInformation("Запуск очистки MP3 файлов старше 1 часа");
            
            try
            {
                // Получаем завершенные задания с MP3 URL, созданные более часа назад
                DateTime expirationThreshold = DateTime.UtcNow.AddHours(-1);
                var expiredJobs = await _jobRepository.GetCompletedJobsWithMp3UrlOlderThanAsync(expirationThreshold);
                
                _logger.LogInformation($"Найдено {expiredJobs.Count()} устаревших MP3 файлов для удаления");

                int deletedCount = 0;
                int errorCount = 0;

                foreach (var job in expiredJobs)
                {
                    try
                    {
                        if (string.IsNullOrEmpty(job.Mp3Url))
                        {
                            continue;
                        }

                        _logger.LogDebug($"Удаление MP3 файла: {job.Mp3Url} (создан: {job.CompletedAt})");
                        
                        // Удаляем файл
                        bool deleted = await _storageService.DeleteFileAsync(job.Mp3Url);
                        
                        if (deleted)
                        {
                            deletedCount++;
                        }
                        else
                        {
                            _logger.LogWarning($"Не удалось удалить MP3 файл: {job.Mp3Url}");
                            errorCount++;
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, $"Ошибка при удалении MP3 файла: {job.Mp3Url}");
                        errorCount++;
                    }
                }
                
                _logger.LogInformation($"Очистка MP3 файлов завершена. Удалено: {deletedCount}, ошибок: {errorCount}");
            }
            catch (Exception ex) when (ex.Message.Contains("relation") && ex.Message.Contains("does not exist"))
            {
                // Обработка ошибки, когда таблица не существует (еще не создана через миграцию)
                _logger.LogWarning("Таблица ConversionJobs еще не создана, пропускаем очистку MP3: {ErrorMessage}", ex.Message);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Непредвиденная ошибка при очистке MP3 файлов");
                throw; // Позволяем Hangfire повторить задачу
            }
        }
    }
} 