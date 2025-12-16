using System.Text.Json.Serialization;

namespace FileConverter.Models
{
    public class JobStatusResponse
    {
        public string JobId { get; set; } = string.Empty;
        
        [JsonConverter(typeof(JsonStringEnumConverter))]
        public ConversionStatus Status { get; set; }
        public string? VideoUrl { get; set; }
        public string? NewVideoUrl { get; set; }
        public string? Mp3Url { get; set; }
        /// <summary>
        /// URL аудио файла в AssemblyAI (upload_url)
        /// </summary>
        public string? AssemblyAiAudioUrl { get; set; }
        /// <summary>
        /// Информация о ключевых кадрах с таймкодами
        /// </summary>
        public List<KeyframeInfo>? Keyframes { get; set; }
        
        /// <summary>
        /// Результат анализа аудиодорожки
        /// </summary>
        public AudioAnalysisData? AudioAnalysis { get; set; }
        
        public string? ErrorMessage { get; set; }
        public double Progress { get; set; }
    }
} 