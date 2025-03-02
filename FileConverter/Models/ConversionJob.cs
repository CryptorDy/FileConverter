namespace FileConverter.Models
{
    public class ConversionJob
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public string VideoUrl { get; set; } = string.Empty;
        public string? Mp3Url { get; set; }
        public ConversionStatus Status { get; set; } = ConversionStatus.Pending;
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime? CompletedAt { get; set; }
        public string? ErrorMessage { get; set; }
        
        // Путь к временным файлам (не сохраняется в БД)
        [System.ComponentModel.DataAnnotations.Schema.NotMapped]
        public string? TempVideoPath { get; set; }
        
        [System.ComponentModel.DataAnnotations.Schema.NotMapped]
        public string? TempMp3Path { get; set; }
        
        // Связь с BatchJob
        public string? BatchId { get; set; }
        
        // Дополнительные метаданные
        public long? FileSizeBytes { get; set; }
        public string? ContentType { get; set; }
        public int ProcessingAttempts { get; set; } = 0;
        public DateTime? LastAttemptAt { get; set; }
    }

    public enum ConversionStatus
    {
        Pending,
        Downloading,
        Converting,
        Uploading,
        Completed,
        Failed
    }
} 