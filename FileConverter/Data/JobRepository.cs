using FileConverter.Models;
using Microsoft.EntityFrameworkCore;

namespace FileConverter.Data
{
    public class JobRepository : IJobRepository
    {
        private readonly AppDbContext _context;
        private readonly ILogger<JobRepository> _logger;

        public JobRepository(AppDbContext context, ILogger<JobRepository> logger)
        {
            _context = context;
            _logger = logger;
        }

        // Методы для работы с отдельными задачами
        public async Task<ConversionJob> CreateJobAsync(ConversionJob job)
        {
            await _context.ConversionJobs.AddAsync(job);
            await _context.SaveChangesAsync();
            return job;
        }

        public async Task<ConversionJob?> GetJobByIdAsync(string jobId)
        {
            return await _context.ConversionJobs.FindAsync(jobId);
        }

        public async Task<ConversionJob> UpdateJobAsync(ConversionJob job)
        {
            _context.ConversionJobs.Update(job);
            await _context.SaveChangesAsync();
            return job;
        }

        public async Task<List<ConversionJob>> GetJobsByStatusAsync(ConversionStatus status, int take = 100)
        {
            return await _context.ConversionJobs
                .Where(j => j.Status == status)
                .OrderBy(j => j.CreatedAt)
                .Take(take)
                .ToListAsync();
        }

        public async Task<List<ConversionJob>> GetAllJobsAsync(int skip = 0, int take = 20)
        {
            return await _context.ConversionJobs
                .OrderByDescending(j => j.CreatedAt)
                .Skip(skip)
                .Take(take)
                .ToListAsync();
        }

        // Методы для работы с пакетными задачами
        public async Task<BatchJob> CreateBatchAsync(BatchJob batch)
        {
            await _context.BatchJobs.AddAsync(batch);
            await _context.SaveChangesAsync();
            return batch;
        }

        public async Task<BatchJob?> GetBatchByIdAsync(string batchId)
        {
            return await _context.BatchJobs
                .Include(b => b.Jobs)
                .FirstOrDefaultAsync(b => b.Id == batchId);
        }

        public async Task<List<ConversionJob>> GetJobsByBatchIdAsync(string batchId)
        {
            return await _context.ConversionJobs
                .Where(j => j.BatchId == batchId)
                .ToListAsync();
        }

        public async Task<BatchJob> UpdateBatchAsync(BatchJob batch)
        {
            _context.BatchJobs.Update(batch);
            await _context.SaveChangesAsync();
            return batch;
        }

        // Специальные методы
        public async Task<ConversionJob> UpdateJobStatusAsync(string jobId, ConversionStatus status, string? mp3Url = null, string? errorMessage = null)
        {
            var job = await _context.ConversionJobs.FindAsync(jobId);
            
            if (job == null)
            {
                throw new KeyNotFoundException($"Задача с ID {jobId} не найдена");
            }
            
            job.Status = status;
            
            if (mp3Url != null)
            {
                job.Mp3Url = mp3Url;
            }
            
            if (errorMessage != null)
            {
                job.ErrorMessage = errorMessage;
            }
            
            job.ProcessingAttempts++;
            job.LastAttemptAt = DateTime.UtcNow;
            
            if (status == ConversionStatus.Completed || status == ConversionStatus.Failed)
            {
                job.CompletedAt = DateTime.UtcNow;
                
                // Если все задачи в пакете завершены, обновляем время завершения пакета
                if (job.BatchId != null)
                {
                    var batch = await _context.BatchJobs.FindAsync(job.BatchId);
                    if (batch != null)
                    {
                        var allJobsCompleted = await _context.ConversionJobs
                            .Where(j => j.BatchId == job.BatchId)
                            .AllAsync(j => j.Status == ConversionStatus.Completed || j.Status == ConversionStatus.Failed);
                            
                        if (allJobsCompleted && batch.CompletedAt == null)
                        {
                            batch.CompletedAt = DateTime.UtcNow;
                            _context.BatchJobs.Update(batch);
                        }
                    }
                }
            }
            
            await _context.SaveChangesAsync();
            return job;
        }

        public async Task<bool> JobExistsWithVideoUrlAsync(string videoUrl)
        {
            return await _context.ConversionJobs
                .AnyAsync(j => j.VideoUrl == videoUrl);
        }

        public async Task<ConversionJob?> GetCompletedJobByVideoUrlAsync(string videoUrl)
        {
            return await _context.ConversionJobs
                .Where(j => j.VideoUrl == videoUrl && j.Status == ConversionStatus.Completed && j.Mp3Url != null)
                .OrderByDescending(j => j.CompletedAt)
                .FirstOrDefaultAsync();
        }
        
        /// <summary>
        /// Получает список завершенных задач, которые были выполнены до указанной даты
        /// </summary>
        /// <param name="date">Дата, до которой нужно получить задачи</param>
        /// <returns>Список задач</returns>
        public async Task<List<ConversionJob>> GetCompletedJobsBeforeDateAsync(DateTime date)
        {
            return await _context.ConversionJobs
                .Where(j => j.Status == ConversionStatus.Completed && 
                       j.CompletedAt.HasValue && 
                       j.CompletedAt.Value <= date &&
                       j.Mp3Url != null)
                .ToListAsync();
        }

        /// <summary>
        /// Получает завершенные задания с MP3 файлами, созданными ранее указанной даты
        /// </summary>
        /// <param name="date">Дата, ранее которой были созданы MP3 файлы</param>
        /// <returns>Список завершенных заданий с MP3 URL</returns>
        public async Task<IEnumerable<ConversionJob>> GetCompletedJobsWithMp3UrlOlderThanAsync(DateTime date)
        {
            return await _context.ConversionJobs
                .Where(j => j.Status == ConversionStatus.Completed
                        && j.CompletedAt.HasValue
                        && j.CompletedAt.Value < date
                        && !string.IsNullOrEmpty(j.Mp3Url))
                .ToListAsync();
        }

        /// <summary>
        /// Получает список заданий в заданном статусе
        /// </summary>
        public async Task<IEnumerable<ConversionJob>> GetJobsByStatusAsync(ConversionStatus status)
        {
            return await _context.ConversionJobs
                .Where(j => j.Status == status)
                .ToListAsync();
        }
    }
} 