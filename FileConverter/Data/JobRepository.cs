using FileConverter.Models;
using Microsoft.EntityFrameworkCore;

namespace FileConverter.Data
{
    public class JobRepository : IJobRepository
    {
        private readonly DbContextFactory _dbContextFactory;
        private readonly ILogger<JobRepository> _logger;

        public JobRepository(DbContextFactory dbContextFactory, ILogger<JobRepository> logger)
        {
            _dbContextFactory = dbContextFactory;
            _logger = logger;
        }

        // Методы для работы с отдельными задачами
        public async Task<ConversionJob> CreateJobAsync(ConversionJob job)
        {
            return await _dbContextFactory.ExecuteWithDbContextAsync(async dbContext =>
            {
                await dbContext.ConversionJobs.AddAsync(job);
                await dbContext.SaveChangesAsync();
                return job;
            });
        }

        public async Task<ConversionJob?> GetJobByIdAsync(string jobId)
        {
            return await _dbContextFactory.ExecuteWithDbContextAsync(async dbContext =>
            {
                return await dbContext.ConversionJobs.FindAsync(jobId);
            });
        }

        public async Task<ConversionJob> UpdateJobAsync(ConversionJob job)
        {
            return await _dbContextFactory.ExecuteWithDbContextAsync(async dbContext =>
            {
                dbContext.ConversionJobs.Update(job);
                await dbContext.SaveChangesAsync();
                return job;
            });
        }

        public async Task<List<ConversionJob>> GetJobsByStatusAsync(ConversionStatus status, int take = 100)
        {
            return await _dbContextFactory.ExecuteWithDbContextAsync(async dbContext =>
            {
                return await dbContext.ConversionJobs
                    .Where(j => j.Status == status)
                    .OrderBy(j => j.CreatedAt)
                    .Take(take)
                    .ToListAsync();
            });
        }

        public async Task<List<ConversionJob>> GetAllJobsAsync(int skip = 0, int take = 20)
        {
            return await _dbContextFactory.ExecuteWithDbContextAsync(async dbContext =>
            {
                return await dbContext.ConversionJobs
                    .OrderByDescending(j => j.CreatedAt)
                    .Skip(skip)
                    .Take(take)
                    .ToListAsync();
            });
        }

        // Методы для пакетных задач
        public async Task<BatchJob> CreateBatchJobAsync(BatchJob batchJob)
        {
            return await _dbContextFactory.ExecuteWithDbContextAsync(async dbContext =>
            {
                await dbContext.BatchJobs.AddAsync(batchJob);
                await dbContext.SaveChangesAsync();
                return batchJob;
            });
        }

        public async Task<BatchJob?> GetBatchJobByIdAsync(string batchId)
        {
            return await _dbContextFactory.ExecuteWithDbContextAsync(async dbContext =>
            {
                return await dbContext.BatchJobs
                    .Include(b => b.Jobs)
                    .FirstOrDefaultAsync(b => b.Id == batchId);
            });
        }

        public async Task<BatchJob> UpdateBatchJobAsync(BatchJob batchJob)
        {
            return await _dbContextFactory.ExecuteWithDbContextAsync(async dbContext =>
            {
                dbContext.BatchJobs.Update(batchJob);
                await dbContext.SaveChangesAsync();
                return batchJob;
            });
        }

        // Специальные методы
        public async Task<ConversionJob> UpdateJobStatusAsync(
            string jobId, 
            ConversionStatus status, 
            string? mp3Url, 
            string? newVideoUrl,
            string? errorMessage)
        {
            return await _dbContextFactory.ExecuteWithDbContextAsync(async dbContext =>
            {
                var job = await dbContext.ConversionJobs.FindAsync(jobId);
                
                if (job == null)
                {
                    throw new KeyNotFoundException($"Задача с ID {jobId} не найдена");
                }
                
                job.Status = status;
                
                job.NewVideoUrl = newVideoUrl ?? job.NewVideoUrl;
                job.Mp3Url = mp3Url ?? job.Mp3Url;
                
                if (errorMessage != null)
                {
                    job.ErrorMessage = errorMessage;
                }
                
                // Для завершенных или проваленных задач устанавливаем дату завершения
                if (status == ConversionStatus.Completed || status == ConversionStatus.Failed)
                {
                    job.CompletedAt = DateTime.UtcNow;
                }
                
                // Для всех кроме создания увеличиваем счетчик попыток
                if (status != ConversionStatus.Pending)
                {
                    job.ProcessingAttempts++;
                    job.LastAttemptAt = DateTime.UtcNow;
                }
                
                await dbContext.SaveChangesAsync();
                return job;
            });
        }

        public async Task<int> GetPendingJobsCountAsync()
        {
            return await _dbContextFactory.ExecuteWithDbContextAsync(async dbContext =>
            {
                return await dbContext.ConversionJobs.CountAsync(j => j.Status == ConversionStatus.Pending);
            });
        }

        public async Task<bool> AnyJobsInProcessingAsync()
        {
            return await _dbContextFactory.ExecuteWithDbContextAsync(async dbContext =>
            {
                return await dbContext.ConversionJobs.AnyAsync(j => 
                    j.Status == ConversionStatus.Downloading || 
                    j.Status == ConversionStatus.Converting || 
                    j.Status == ConversionStatus.Uploading);
            });
        }

        public async Task UpdateJobDurationAsync(string jobId, double durationSeconds)
        {
            await _dbContextFactory.ExecuteWithDbContextAsync(async dbContext =>
            {
                var job = await dbContext.ConversionJobs.FirstOrDefaultAsync(j => j.Id == jobId);
                if (job != null)
                {
                    job.DurationSeconds = durationSeconds;
                    await dbContext.SaveChangesAsync();
                }
            });
        }

        public async Task UpdateJobKeyframesAsync(string jobId, List<KeyframeInfo> keyframes)
        {
            await _dbContextFactory.ExecuteWithDbContextAsync(async dbContext =>
            {
                var job = await dbContext.ConversionJobs.FirstOrDefaultAsync(j => j.Id == jobId);
                if (job != null)
                {
                    job.Keyframes = keyframes;
                    await dbContext.SaveChangesAsync();
                }
            });
        }

        public async Task<List<ConversionJob>> GetJobsByBatchIdAsync(string batchId)
        {
            return await _dbContextFactory.ExecuteWithDbContextAsync(async dbContext =>
            {
                return await dbContext.ConversionJobs
                    .Where(j => j.BatchId == batchId)
                    .OrderBy(j => j.CreatedAt)
                    .ToListAsync();
            });
        }
        
        /// <summary>
        /// Получает завершенные задания с MP3 файлами, созданными ранее указанной даты
        /// </summary>
        /// <param name="date">Дата, ранее которой были созданы MP3 файлы</param>
        /// <returns>Список завершенных заданий с MP3 URL</returns>
        public async Task<IEnumerable<ConversionJob>> GetCompletedJobsWithMp3UrlOlderThanAsync(DateTime date)
        {
            return await _dbContextFactory.ExecuteWithDbContextAsync(async dbContext =>
            {
                return await dbContext.ConversionJobs
                    .Where(j => j.Status == ConversionStatus.Completed
                            && j.CompletedAt.HasValue
                            && j.CompletedAt.Value < date
                            && !string.IsNullOrEmpty(j.Mp3Url))
                    .ToListAsync();
            });
        }

        /// <summary>
        /// Получает список заданий, которые были в процессе, но не обновлялись дольше указанного времени
        /// </summary>
        public async Task<List<ConversionJob>> GetStaleJobsAsync(TimeSpan maxAge)
        {
            DateTime threshold = DateTime.UtcNow.Subtract(maxAge);
            
            return await _dbContextFactory.ExecuteWithDbContextAsync(async dbContext =>
            {
                return await dbContext.ConversionJobs
                    .Where(j => (j.Status == ConversionStatus.Downloading || 
                                j.Status == ConversionStatus.Converting || 
                                j.Status == ConversionStatus.Uploading) &&
                                j.LastAttemptAt < threshold)
                    .ToListAsync();
            });
        }
        
        /// <summary>
        /// Получает количество заданий с указанными статусами
        /// </summary>
        /// <param name="statuses">Список статусов для подсчета</param>
        /// <returns>Количество заданий</returns>
        public async Task<int> GetJobsByStatusesCountAsync(ConversionStatus[] statuses)
        {
            return await _dbContextFactory.ExecuteWithDbContextAsync(async dbContext =>
            {
                return await dbContext.ConversionJobs
                    .Where(j => statuses.Contains(j.Status))
                    .CountAsync();
            });
        }
        
        /// <summary>
        /// Атомарно проверяет статус задачи и обновляет его если он соответствует ожидаемому
        /// </summary>
        public async Task<bool> TryUpdateJobStatusIfAsync(string jobId, ConversionStatus expectedStatus, ConversionStatus newStatus)
        {
            return await _dbContextFactory.ExecuteWithDbContextAsync(async dbContext =>
            {
                // Используем ExecuteUpdateAsync для атомарного обновления
                var rowsAffected = await dbContext.ConversionJobs
                    .Where(j => j.Id == jobId && j.Status == expectedStatus)
                    .ExecuteUpdateAsync(updates => updates
                        .SetProperty(j => j.Status, newStatus)
                        .SetProperty(j => j.LastAttemptAt, DateTime.UtcNow));
                
                return rowsAffected > 0;
            });
        }
        
        /// <summary>
        /// Обновляет данные анализа аудио для задачи
        /// </summary>
        public async Task UpdateJobAudioAnalysisAsync(string jobId, AudioAnalysisData audioAnalysis)
        {
            await _dbContextFactory.ExecuteWithDbContextAsync(async dbContext =>
            {
                var job = await dbContext.ConversionJobs.FirstOrDefaultAsync(j => j.Id == jobId);
                if (job != null)
                {
                    job.AudioAnalysis = audioAnalysis;
                    await dbContext.SaveChangesAsync();
                }
            });
        }
    }
} 