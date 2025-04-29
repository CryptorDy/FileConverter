namespace FileConverter.Models;

/// <summary>
/// Настройки прокси-сервера для HTTP запросов
/// </summary>
public class ProxySettings
{
    /// <summary>
    /// Включено ли использование прокси
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Адрес прокси-сервера
    /// </summary>
    public string Host { get; set; } = string.Empty;

    /// <summary>
    /// Порт прокси-сервера
    /// </summary>
    public int Port { get; set; }

    /// <summary>
    /// Имя пользователя для аутентификации на прокси (если требуется)
    /// </summary>
    public string? Username { get; set; }

    /// <summary>
    /// Пароль для аутентификации на прокси (если требуется)
    /// </summary>
    public string? Password { get; set; }

    /// <summary>
    /// Список прокси-серверов. Если указан, предпочтителен к использованию.
    /// </summary>
    public List<ProxyServer>? Servers { get; set; }

    // ===== Поля ниже оставлены для обратной совместимости с прежней конфигурацией =====
} 