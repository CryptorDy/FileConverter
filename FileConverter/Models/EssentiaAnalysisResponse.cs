using System.Text.Json.Serialization;

namespace FileConverter.Models
{
    /// <summary>
    /// Безопасная модель ответа от Essentia анализа
    /// </summary>
    public class EssentiaAnalysisResponse
    {
        [JsonPropertyName("error")]
        public string? Error { get; set; }

        [JsonPropertyName("audio_analysis")]
        public AudioAnalysisData? AudioAnalysis { get; set; }
    }
} 