using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Net;
using System.Net.Mail;
using System.Text;
using System.Collections.Concurrent;

namespace FileConverter.Services;

public class EmailNotificationOptions
{
    public string SmtpServer { get; set; } = "smtp.gmail.com";
    public int SmtpPort { get; set; } = 587;
    public string SmtpUsername { get; set; } = string.Empty;
    public string SmtpPassword { get; set; } = string.Empty;
    public string FromEmail { get; set; } = string.Empty;
    public string FromName { get; set; } = "FileConverter Proxy Monitor";
    public string AdminEmail { get; set; } = string.Empty;
    public bool EnableSsl { get; set; } = true;
    public bool EnableNotifications { get; set; } = true;
    
    // Настройки защиты от спама
    public int MaxFailureNotificationsPerHour { get; set; } = 10;
    public int MaxRecoveryNotificationsPerHour { get; set; } = 5;
    public int MaxCriticalNotificationsPerHour { get; set; } = 3;
    public int NotificationCooldownMinutes { get; set; } = 30;
}

public class EmailNotificationService
{
    private readonly ILogger<EmailNotificationService> _logger;
    private readonly EmailNotificationOptions _options;
    
    // Кэш для защиты от спама
    private readonly ConcurrentDictionary<string, DateTime> _lastNotificationTimes = new();
    private readonly ConcurrentDictionary<string, int> _notificationCounts = new();
    private readonly object _countResetLock = new object();
    private DateTime _lastCountReset = DateTime.UtcNow;

    public EmailNotificationService(IOptions<EmailNotificationOptions> options, ILogger<EmailNotificationService> logger)
    {
        _options = options.Value;
        _logger = logger;
        
        // Валидация конфигурации
        ValidateConfiguration();
    }

    /// <summary>
    /// Валидирует конфигурацию email уведомлений
    /// </summary>
    private void ValidateConfiguration()
    {
        if (_options.EnableNotifications)
        {
            var errors = new List<string>();
            
            if (string.IsNullOrEmpty(_options.SmtpServer))
                errors.Add("SmtpServer не настроен");
                
            if (_options.SmtpPort <= 0 || _options.SmtpPort > 65535)
                errors.Add("SmtpPort должен быть в диапазоне 1-65535");
                
            if (string.IsNullOrEmpty(_options.SmtpUsername))
                errors.Add("SmtpUsername не настроен");
                
            if (string.IsNullOrEmpty(_options.SmtpPassword))
                errors.Add("SmtpPassword не настроен");
                
            if (string.IsNullOrEmpty(_options.FromEmail))
                errors.Add("FromEmail не настроен");
                
            if (string.IsNullOrEmpty(_options.AdminEmail))
                errors.Add("AdminEmail не настроен");
                
            if (_options.MaxFailureNotificationsPerHour <= 0)
                errors.Add("MaxFailureNotificationsPerHour должен быть больше 0");
                
            if (_options.MaxRecoveryNotificationsPerHour <= 0)
                errors.Add("MaxRecoveryNotificationsPerHour должен быть больше 0");
                
            if (_options.MaxCriticalNotificationsPerHour <= 0)
                errors.Add("MaxCriticalNotificationsPerHour должен быть больше 0");
                
            if (_options.NotificationCooldownMinutes <= 0)
                errors.Add("NotificationCooldownMinutes должен быть больше 0");

            if (errors.Any())
            {
                var errorMessage = "Ошибки в конфигурации EmailNotifications: " + string.Join(", ", errors);
                _logger.LogError(errorMessage);
                throw new InvalidOperationException(errorMessage);
            }
            
            _logger.LogInformation("Email уведомления настроены для {AdminEmail} через {SmtpServer}:{SmtpPort}", 
                _options.AdminEmail, _options.SmtpServer, _options.SmtpPort);
        }
        else
        {
            _logger.LogInformation("Email уведомления отключены");
        }
    }

    /// <summary>
    /// Отправляет уведомление о проблеме с прокси
    /// </summary>
    public async Task SendProxyFailureNotificationAsync(string proxyHost, int proxyPort, string error, int errorCount, int threshold)
    {
        if (!_options.EnableNotifications || string.IsNullOrEmpty(_options.AdminEmail))
        {
            _logger.LogDebug("Email уведомления отключены или не настроен админский email");
            return;
        }

        var proxyKey = $"failure_{proxyHost}:{proxyPort}";
        
        // Проверяем защиту от спама
        if (!ShouldSendNotification(proxyKey, _options.MaxFailureNotificationsPerHour))
        {
            _logger.LogDebug("Пропускаем уведомление о проблеме с прокси {Host}:{Port} - слишком часто", proxyHost, proxyPort);
            return;
        }

        try
        {
            var subject = $"⚠️ Проблема с прокси {proxyHost}:{proxyPort}";
            var body = GenerateProxyFailureEmailBody(proxyHost, proxyPort, error, errorCount, threshold);

            await SendEmailAsync(_options.AdminEmail, subject, body);
            
            // Обновляем время последнего уведомления
            _lastNotificationTimes[proxyKey] = DateTime.UtcNow;
            IncrementNotificationCount(proxyKey);
            
            _logger.LogInformation("Отправлено уведомление о проблеме с прокси {Host}:{Port} на {Email}", 
                proxyHost, proxyPort, _options.AdminEmail);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Ошибка при отправке уведомления о проблеме с прокси {Host}:{Port}", proxyHost, proxyPort);
        }
    }

    /// <summary>
    /// Отправляет уведомление о восстановлении прокси
    /// </summary>
    public async Task SendProxyRecoveryNotificationAsync(string proxyHost, int proxyPort)
    {
        if (!_options.EnableNotifications || string.IsNullOrEmpty(_options.AdminEmail))
        {
            return;
        }

        var proxyKey = $"recovery_{proxyHost}:{proxyPort}";
        
        // Проверяем защиту от спама
        if (!ShouldSendNotification(proxyKey, _options.MaxRecoveryNotificationsPerHour))
        {
            _logger.LogDebug("Пропускаем уведомление о восстановлении прокси {Host}:{Port} - слишком часто", proxyHost, proxyPort);
            return;
        }

        try
        {
            var subject = $"✅ Прокси {proxyHost}:{proxyPort} восстановлен";
            var body = GenerateProxyRecoveryEmailBody(proxyHost, proxyPort);

            await SendEmailAsync(_options.AdminEmail, subject, body);
            
            // Обновляем время последнего уведомления
            _lastNotificationTimes[proxyKey] = DateTime.UtcNow;
            IncrementNotificationCount(proxyKey);
            
            _logger.LogInformation("Отправлено уведомление о восстановлении прокси {Host}:{Port} на {Email}", 
                proxyHost, proxyPort, _options.AdminEmail);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Ошибка при отправке уведомления о восстановлении прокси {Host}:{Port}", proxyHost, proxyPort);
        }
    }

    /// <summary>
    /// Отправляет уведомление о критической ситуации с прокси
    /// </summary>
    public async Task SendCriticalProxyNotificationAsync(int totalProxies, int availableProxies, int failedProxies)
    {
        if (!_options.EnableNotifications || string.IsNullOrEmpty(_options.AdminEmail))
        {
            return;
        }

        var criticalKey = "critical_situation";
        
        // Проверяем защиту от спама
        if (!ShouldSendNotification(criticalKey, _options.MaxCriticalNotificationsPerHour))
        {
            _logger.LogDebug("Пропускаем критическое уведомление - слишком часто");
            return;
        }

        try
        {
            var subject = $"🚨 Критическая ситуация с прокси!";
            var body = GenerateCriticalProxyEmailBody(totalProxies, availableProxies, failedProxies);

            await SendEmailAsync(_options.AdminEmail, subject, body);
            
            // Обновляем время последнего уведомления
            _lastNotificationTimes[criticalKey] = DateTime.UtcNow;
            IncrementNotificationCount(criticalKey);
            
            _logger.LogWarning("Отправлено критическое уведомление о прокси на {Email}", _options.AdminEmail);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Ошибка при отправке критического уведомления о прокси");
        }
    }

    /// <summary>
    /// Проверяет, следует ли отправлять уведомление (защита от спама)
    /// </summary>
    private bool ShouldSendNotification(string key, int maxPerHour)
    {
        // Сбрасываем счетчики каждый час
        ResetCountersIfNeeded();
        
        var now = DateTime.UtcNow;
        
        // Проверяем время последнего уведомления
        if (_lastNotificationTimes.TryGetValue(key, out var lastTime))
        {
            var timeSinceLastNotification = now - lastTime;
            if (timeSinceLastNotification.TotalMinutes < _options.NotificationCooldownMinutes)
            {
                return false;
            }
        }
        
        // Проверяем количество уведомлений в час
        var count = _notificationCounts.GetOrAdd(key, 0);
        return count < maxPerHour;
    }

    /// <summary>
    /// Увеличивает счетчик уведомлений
    /// </summary>
    private void IncrementNotificationCount(string key)
    {
        _notificationCounts.AddOrUpdate(key, 1, (k, v) => v + 1);
    }

    /// <summary>
    /// Сбрасывает счетчики каждый час
    /// </summary>
    private void ResetCountersIfNeeded()
    {
        var now = DateTime.UtcNow;
        lock (_countResetLock)
        {
            if ((now - _lastCountReset).TotalHours >= 1)
            {
                _notificationCounts.Clear();
                _lastCountReset = now;
                _logger.LogDebug("Счетчики уведомлений сброшены");
            }
        }
    }

    /// <summary>
    /// Отправляет email
    /// </summary>
    private async Task SendEmailAsync(string toEmail, string subject, string body)
    {
        using var client = new SmtpClient(_options.SmtpServer, _options.SmtpPort)
        {
            EnableSsl = _options.EnableSsl,
            Credentials = new NetworkCredential(_options.SmtpUsername, _options.SmtpPassword),
            Timeout = 30000 // 30 секунд таймаут
        };

        using var message = new MailMessage
        {
            From = new MailAddress(_options.FromEmail, _options.FromName),
            Subject = subject,
            Body = body,
            IsBodyHtml = true,
            Priority = MailPriority.High
        };

        message.To.Add(toEmail);

        await client.SendMailAsync(message);
    }

    /// <summary>
    /// Генерирует тело email для ошибки прокси
    /// </summary>
    private string GenerateProxyFailureEmailBody(string proxyHost, int proxyPort, string error, int errorCount, int threshold)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<html><body>");
        sb.AppendLine("<h2>⚠️ Проблема с прокси-сервером</h2>");
        sb.AppendLine("<table style='border-collapse: collapse; width: 100%;'>");
        sb.AppendLine("<tr><td style='padding: 8px; border: 1px solid #ddd;'><strong>Прокси:</strong></td><td style='padding: 8px; border: 1px solid #ddd;'>" + proxyHost + ":" + proxyPort + "</td></tr>");
        sb.AppendLine("<tr><td style='padding: 8px; border: 1px solid #ddd;'><strong>Ошибка:</strong></td><td style='padding: 8px; border: 1px solid #ddd;'>" + error + "</td></tr>");
        sb.AppendLine("<tr><td style='padding: 8px; border: 1px solid #ddd;'><strong>Количество ошибок:</strong></td><td style='padding: 8px; border: 1px solid #ddd;'>" + errorCount + "/" + threshold + "</td></tr>");
        sb.AppendLine("<tr><td style='padding: 8px; border: 1px solid #ddd;'><strong>Время:</strong></td><td style='padding: 8px; border: 1px solid #ddd;'>" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "</td></tr>");
        sb.AppendLine("</table>");
        
        if (errorCount >= threshold)
        {
            sb.AppendLine("<p style='color: red; font-weight: bold;'>🚨 Прокси помечен как недоступный!</p>");
        }
        else
        {
            sb.AppendLine("<p style='color: orange;'>⚠️ Прокси близок к отключению!</p>");
        }
        
        sb.AppendLine("<p>Рекомендуемые действия:</p>");
        sb.AppendLine("<ul>");
        sb.AppendLine("<li>Проверить доступность прокси-сервера</li>");
        sb.AppendLine("<li>Проверить настройки аутентификации</li>");
        sb.AppendLine("<li>Связаться с провайдером прокси</li>");
        sb.AppendLine("<li>Добавить резервные прокси в систему</li>");
        sb.AppendLine("</ul>");
        sb.AppendLine("</body></html>");
        
        return sb.ToString();
    }

    /// <summary>
    /// Генерирует тело email для восстановления прокси
    /// </summary>
    private string GenerateProxyRecoveryEmailBody(string proxyHost, int proxyPort)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<html><body>");
        sb.AppendLine("<h2>✅ Прокси-сервер восстановлен</h2>");
        sb.AppendLine("<table style='border-collapse: collapse; width: 100%;'>");
        sb.AppendLine("<tr><td style='padding: 8px; border: 1px solid #ddd;'><strong>Прокси:</strong></td><td style='padding: 8px; border: 1px solid #ddd;'>" + proxyHost + ":" + proxyPort + "</td></tr>");
        sb.AppendLine("<tr><td style='padding: 8px; border: 1px solid #ddd;'><strong>Статус:</strong></td><td style='padding: 8px; border: 1px solid #ddd; color: green;'>✅ Доступен</td></tr>");
        sb.AppendLine("<tr><td style='padding: 8px; border: 1px solid #ddd;'><strong>Время восстановления:</strong></td><td style='padding: 8px; border: 1px solid #ddd;'>" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "</td></tr>");
        sb.AppendLine("</table>");
        sb.AppendLine("<p style='color: green;'>Прокси снова доступен и используется системой.</p>");
        sb.AppendLine("</body></html>");
        
        return sb.ToString();
    }

    /// <summary>
    /// Генерирует тело email для критической ситуации
    /// </summary>
    private string GenerateCriticalProxyEmailBody(int totalProxies, int availableProxies, int failedProxies)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<html><body>");
        sb.AppendLine("<h2>🚨 Критическая ситуация с прокси!</h2>");
        sb.AppendLine("<table style='border-collapse: collapse; width: 100%;'>");
        sb.AppendLine("<tr><td style='padding: 8px; border: 1px solid #ddd;'><strong>Всего прокси:</strong></td><td style='padding: 8px; border: 1px solid #ddd;'>" + totalProxies + "</td></tr>");
        sb.AppendLine("<tr><td style='padding: 8px; border: 1px solid #ddd;'><strong>Доступных:</strong></td><td style='padding: 8px; border: 1px solid #ddd; color: green;'>" + availableProxies + "</td></tr>");
        sb.AppendLine("<tr><td style='padding: 8px; border: 1px solid #ddd;'><strong>Недоступных:</strong></td><td style='padding: 8px; border: 1px solid #ddd; color: red;'>" + failedProxies + "</td></tr>");
        sb.AppendLine("<tr><td style='padding: 8px; border: 1px solid #ddd;'><strong>Процент недоступных:</strong></td><td style='padding: 8px; border: 1px solid #ddd; color: red;'>" + (totalProxies > 0 ? Math.Round((double)failedProxies / totalProxies * 100, 1) : 0) + "%</td></tr>");
        sb.AppendLine("<tr><td style='padding: 8px; border: 1px solid #ddd;'><strong>Время:</strong></td><td style='padding: 8px; border: 1px solid #ddd;'>" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "</td></tr>");
        sb.AppendLine("</table>");
        
        sb.AppendLine("<p style='color: red; font-weight: bold;'>🚨 ТРЕБУЕТСЯ НЕМЕДЛЕННОЕ ВМЕШАТЕЛЬСТВО!</p>");
        sb.AppendLine("<p>Рекомендуемые действия:</p>");
        sb.AppendLine("<ul>");
        sb.AppendLine("<li>Проверить все недоступные прокси</li>");
        sb.AppendLine("<li>Добавить новые прокси в систему</li>");
        sb.AppendLine("<li>Связаться с провайдерами прокси</li>");
        sb.AppendLine("<li>Рассмотреть возможность перехода на прямые соединения</li>");
        sb.AppendLine("</ul>");
        sb.AppendLine("</body></html>");
        
        return sb.ToString();
    }

    /// <summary>
    /// Тестирует соединение с SMTP сервером
    /// </summary>
    public async Task<bool> TestConnectionAsync()
    {
        if (!_options.EnableNotifications)
        {
            _logger.LogWarning("Email уведомления отключены");
            return false;
        }

        try
        {
            using var client = new SmtpClient(_options.SmtpServer, _options.SmtpPort)
            {
                EnableSsl = _options.EnableSsl,
                Credentials = new NetworkCredential(_options.SmtpUsername, _options.SmtpPassword),
                Timeout = 10000 // 10 секунд для теста
            };

            // Отправляем тестовое письмо
            using var message = new MailMessage
            {
                From = new MailAddress(_options.FromEmail, _options.FromName),
                Subject = "🧪 Тест соединения - FileConverter Proxy Monitor",
                Body = "<html><body><h2>✅ Соединение с SMTP сервером работает!</h2><p>Время теста: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "</p></body></html>",
                IsBodyHtml = true,
                Priority = MailPriority.Low
            };

            message.To.Add(_options.AdminEmail);

            await client.SendMailAsync(message);
            
            _logger.LogInformation("Тест SMTP соединения успешен");
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Ошибка при тестировании SMTP соединения");
            return false;
        }
    }

    /// <summary>
    /// Получает статистику уведомлений
    /// </summary>
    public object GetNotificationStats()
    {
        return new
        {
            enabled = _options.EnableNotifications,
            adminEmail = _options.AdminEmail,
            smtpServer = _options.SmtpServer,
            lastCountReset = _lastCountReset,
            notificationCounts = _notificationCounts.ToDictionary(kvp => kvp.Key, kvp => kvp.Value),
            lastNotificationTimes = _lastNotificationTimes.ToDictionary(kvp => kvp.Key, kvp => kvp.Value)
        };
    }
}
