using System.Text.Json.Serialization;
using System.ComponentModel.DataAnnotations.Schema;

namespace FileConverter.Models
{
    public class MediaStorageItem
    {
        public string Id { get; set; } = string.Empty;
        public string VideoHash { get; set; } = string.Empty;
        public string VideoUrl { get; set; } = string.Empty;
        public string? AudioUrl { get; set; }
        /// <summary>
        /// Информация о ключевых кадрах с таймкодами
        /// </summary>
        public List<KeyframeInfo>? Keyframes { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime? LastAccessedAt { get; set; }
        public long FileSizeBytes { get; set; }
        public string ContentType { get; set; } = string.Empty;
        
        /// <summary>
        /// Длительность видео в секундах
        /// </summary>
        public double? DurationSeconds { get; set; }
        
        /// <summary>
        /// Результат анализа аудиодорожки
        /// </summary>
        [Column(TypeName = "jsonb")]
        public AudioAnalysisData? AudioAnalysis { get; set; }
    }
} 