using System.Text.Json.Serialization;

namespace FileConverter.Models
{
    /// <summary>
    /// Модель ответа для статуса пакета задач
    /// </summary>
    public class BatchStatusResponse
    {
        /// <summary>
        /// Идентификатор пакета задач
        /// </summary>
        public string BatchId { get; set; } = string.Empty;
        
        /// <summary>
        /// Общий статус всего пакета задач
        /// </summary>
        [JsonConverter(typeof(JsonStringEnumConverter))]
        public BatchStatus Status { get; set; }
        
        /// <summary>
        /// Список статусов отдельных задач в пакете
        /// </summary>
        public List<JobStatusResponse> Jobs { get; set; } = new List<JobStatusResponse>();
        
        /// <summary>
        /// Общий процент выполнения
        /// </summary>
        public double Progress { get; set; }
    }
    
    /// <summary>
    /// Перечисление для общего статуса пакета задач
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum BatchStatus
    {
        /// <summary>
        /// Обработка продолжается
        /// </summary>
        Pending,
        
        /// <summary>
        /// Все задачи обработаны (успешно или с ошибками)
        /// </summary>
        Completed,
        
        /// <summary>
        /// Все задачи завершились с ошибкой
        /// </summary>
        Failed
    }
} 