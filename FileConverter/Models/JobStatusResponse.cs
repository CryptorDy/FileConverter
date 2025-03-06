using System.Text.Json.Serialization;

namespace FileConverter.Models
{
    public class JobStatusResponse
    {
        public string JobId { get; set; } = string.Empty;
        
        [JsonConverter(typeof(JsonStringEnumConverter))]
        public ConversionStatus Status { get; set; }
        
        public string? Mp3Url { get; set; }
        public string? ErrorMessage { get; set; }
        public double Progress { get; set; }
    }
} 