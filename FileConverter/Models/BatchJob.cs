namespace FileConverter.Models
{
    public class BatchJob
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime? CompletedAt { get; set; }
        
        // Навигационное свойство для связи с задачами
        public virtual List<ConversionJob> Jobs { get; set; } = new List<ConversionJob>();
    }
} 