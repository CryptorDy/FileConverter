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
        }
    }
} 