using FileConverter.Models;
using Microsoft.EntityFrameworkCore;

namespace FileConverter.Data
{
    public class AppDbContext : DbContext
    {
        public AppDbContext(DbContextOptions<AppDbContext> options) : base(options)
        {
        }

        public DbSet<ConversionJob> ConversionJobs { get; set; }
        public DbSet<BatchJob> BatchJobs { get; set; }
        public DbSet<MediaStorageItem> MediaItems { get; set; }
        public DbSet<ConversionLogEvent> ConversionLogs { get; set; }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            // Настраиваем ConversionJob
            modelBuilder.Entity<ConversionJob>(entity =>
            {
                entity.HasKey(e => e.Id);
                entity.Property(e => e.VideoUrl).IsRequired();
                entity.Property(e => e.CreatedAt).IsRequired();
                entity.Property(e => e.Status).IsRequired();
                
                // Индексы для быстрого поиска
                entity.HasIndex(e => e.Status);
                entity.HasIndex(e => e.CreatedAt);
                
                // Связь с BatchJob (многие к одному)
                entity.HasOne<BatchJob>()
                     .WithMany(b => b.Jobs)
                     .HasForeignKey(j => j.BatchId)
                     .IsRequired(false)
                     .OnDelete(DeleteBehavior.SetNull);
            });

            // Настраиваем BatchJob
            modelBuilder.Entity<BatchJob>(entity =>
            {
                entity.HasKey(e => e.Id);
                entity.Property(e => e.CreatedAt).IsRequired();
            });
            
            // Настраиваем MediaStorageItem
            modelBuilder.Entity<MediaStorageItem>(entity =>
            {
                entity.HasKey(e => e.Id);
                entity.Property(e => e.VideoHash).IsRequired();
                entity.Property(e => e.VideoUrl).IsRequired();
                entity.Property(e => e.CreatedAt).IsRequired();
                entity.Property(e => e.ContentType).IsRequired();
                
                // Настраиваем Keyframes как JSON поле
                entity.Property(e => e.Keyframes)
                    .HasColumnType("jsonb");
                
                // Уникальный индекс по хешу видео
                entity.HasIndex(e => e.VideoHash).IsUnique();
            });
            
            // Настраиваем ConversionLogEvent
            modelBuilder.Entity<ConversionLogEvent>(entity =>
            {
                entity.HasKey(e => e.Id);
                entity.Property(e => e.JobId).IsRequired();
                entity.Property(e => e.Timestamp).IsRequired();
                entity.Property(e => e.EventType).IsRequired();
                entity.Property(e => e.JobStatus).IsRequired();
                entity.Property(e => e.Message).IsRequired();
                
                // Индексы для быстрого поиска
                entity.HasIndex(e => e.JobId);
                entity.HasIndex(e => e.BatchId);
                entity.HasIndex(e => e.Timestamp);
                entity.HasIndex(e => e.EventType);
                entity.HasIndex(e => new { e.JobId, e.Timestamp });
            });
        }
    }
} 