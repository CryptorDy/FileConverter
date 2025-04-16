using System.Text.Json.Serialization;

namespace FileConverter.Models
{
    /// <summary>
    /// Расширенная информация о результате конвертации видео
    /// </summary>
    public class ConversionResultInfo
    {
        /// <summary>
        /// Идентификатор задачи
        /// </summary>
        public string JobId { get; set; } = string.Empty;
        
        /// <summary>
        /// Исходный URL видео
        /// </summary>
        public string OldUrl { get; set; } = string.Empty;
        
        /// <summary>
        /// Новая ссылка на видео в хранилище
        /// </summary>
        public string? NewUrl { get; set; }
        
        /// <summary>
        /// Ссылка на MP3 файл
        /// </summary>
        public string? Mp3Url { get; set; }
        
        /// <summary>
        /// Статус конвертации
        /// </summary>
        [JsonConverter(typeof(JsonStringEnumConverter))]
        public ConversionStatus Status { get; set; }
        
        /// <summary>
        /// Сообщение об ошибке (если есть)
        /// </summary>
        public string? ErrorMessage { get; set; }
    }
} 