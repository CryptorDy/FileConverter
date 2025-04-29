namespace FileConverter.Models;

/// <summary>
/// Модель для хранения информации о прокси-сервере
/// </summary>
public class ProxyServer
{
    /// <summary>
    /// Адрес прокси-сервера
    /// </summary>
    public string Host { get; set; } = string.Empty;

    /// <summary>
    /// Порт прокси-сервера
    /// </summary>
    public int Port { get; set; }

    /// <summary>
    /// Имя пользователя для аутентификации (если требуется)
    /// </summary>
    public string? Username { get; set; }

    /// <summary>
    /// Пароль для аутентификации (если требуется)
    /// </summary>
    public string? Password { get; set; }

    /// <summary>
    /// Флаг доступности прокси (устанавливается динамически)
    /// </summary>
    public bool IsAvailable { get; set; } = true;

    /// <summary>
    /// Время последней проверки доступности
    /// </summary>
    public DateTime LastChecked { get; set; } = DateTime.MinValue;

    /// <summary>
    /// Количество последовательных ошибок с использованием этого прокси
    /// </summary>
    public int ErrorCount { get; set; } = 0;
} 