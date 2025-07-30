using System.Text.Json.Serialization;

namespace FileConverter.Models
{
    /// <summary>
    /// Данные анализа аудио
    /// </summary>
    public class AudioAnalysisData
    {
        /// <summary>
        /// Темп в ударах в минуту
        /// </summary>
        public float tempo_bpm { get; set; }
        
        /// <summary>
        /// Уверенность в точности анализа (0.0 - 1.0)
        /// </summary>
        public float confidence { get; set; }
        
        /// <summary>
        /// Временные метки битов в секундах
        /// </summary>
        public float[] beat_timestamps_sec { get; set; } = Array.Empty<float>();
        
        /// <summary>
        /// Интервалы между битами в секундах
        /// </summary>
        public float[] bpm_intervals { get; set; } = Array.Empty<float>();
        
        /// <summary>
        /// Количество обнаруженных битов
        /// </summary>
        public int beats_detected { get; set; }
        
        /// <summary>
        /// Регулярность ритма (0.0 - 1.0, где 1.0 - идеально регулярный)
        /// </summary>
        public double rhythm_regularity { get; set; }
    }
} 