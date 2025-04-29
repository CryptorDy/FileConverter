namespace FileConverter.Models;

/// <summary>
/// Класс для хранения статистики использования прокси
/// </summary>
public class ProxyStats
{
    /// <summary>
    /// Общее количество запросов через этот прокси
    /// </summary>
    public int RequestCount { get; set; } = 0;
    
    /// <summary>
    /// Количество успешных запросов
    /// </summary>
    public int SuccessCount { get; set; } = 0;
    
    /// <summary>
    /// Количество ошибок
    /// </summary>
    public int ErrorCount { get; set; } = 0;
    
    /// <summary>
    /// Общее количество переданных байт через этот прокси
    /// </summary>
    public long BytesTransferred { get; set; } = 0;
    
    /// <summary>
    /// Время последнего использования прокси
    /// </summary>
    public DateTime LastUsed { get; set; } = DateTime.UtcNow;
} 