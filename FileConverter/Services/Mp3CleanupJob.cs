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
            _logger.LogInformation("Starting cleanup of MP3 files older than 1 hour");
            
            try
            {
                // Get completed jobs with MP3 URL created more than an hour ago
                DateTime expirationThreshold = DateTime.UtcNow.AddHours(-1);
                var expiredJobs = await _jobRepository.GetCompletedJobsWithMp3UrlOlderThanAsync(expirationThreshold);
                
                _logger.LogInformation($"Found {expiredJobs.Count()} expired MP3 files to delete");

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

                        _logger.LogDebug($"Deleting MP3 file: {job.Mp3Url} (created: {job.CompletedAt})");
                        
                        // Delete the file
                        bool deleted = await _storageService.DeleteFileAsync(job.Mp3Url);
                        
                        if (deleted)
                        {
                            deletedCount++;
                        }
                        else
                        {
                            _logger.LogWarning($"Failed to delete MP3 file: {job.Mp3Url}");
                            errorCount++;
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, $"Error deleting MP3 file: {job.Mp3Url}");
                        errorCount++;
                    }
                }
                
                _logger.LogInformation($"MP3 file cleanup finished. Deleted: {deletedCount}, errors: {errorCount}");
            }
            catch (Exception ex) when (ex.Message.Contains("relation") && ex.Message.Contains("does not exist"))
            {
                // Handle error when the table does not exist (not yet created via migration)
                _logger.LogWarning("ConversionJobs table not created yet, skipping MP3 cleanup: {ErrorMessage}", ex.Message);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error during MP3 file cleanup");
                throw; // Allow Hangfire to retry the job
            }
        }
    }
} 