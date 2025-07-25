using System.Text.Json;
using System.Text.Json.Serialization;

namespace FileConverter.Models
{
    /// <summary>
    /// Информация о ключевом кадре с таймкодом
    /// </summary>
    public class KeyframeInfo
    {
        /// <summary>
        /// URL ключевого кадра в хранилище
        /// </summary>
        public string Url { get; set; } = string.Empty;
        
        /// <summary>
        /// Таймкод кадра в видео
        /// </summary>
        [JsonConverter(typeof(TimeSpanConverter))]
        public TimeSpan Timestamp { get; set; }
        
        /// <summary>
        /// Номер кадра в последовательности
        /// </summary>
        public int FrameNumber { get; set; }
        

    }
    
    /// <summary>
    /// Конвертер для сериализации TimeSpan в JSON
    /// </summary>
    public class TimeSpanConverter : JsonConverter<TimeSpan>
    {
        public override TimeSpan Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            var value = reader.GetString();
            return TimeSpan.TryParse(value, out var result) ? result : TimeSpan.Zero;
        }

        public override void Write(Utf8JsonWriter writer, TimeSpan value, JsonSerializerOptions options)
        {
            writer.WriteStringValue(value.ToString(@"hh\:mm\:ss\.fff"));
        }
    }
} 