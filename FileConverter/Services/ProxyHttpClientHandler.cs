using FileConverter.Models;
using System.Net;

namespace FileConverter.Services;

public class ProxyHttpClientHandler : HttpClientHandler
{
    private readonly ILogger<ProxyHttpClientHandler> _logger;
    
    public ProxyHttpClientHandler(IConfiguration configuration, ILogger<ProxyHttpClientHandler> logger)
    {
        _logger = logger;
        
        var proxySettings = configuration.GetSection("Proxy").Get<ProxySettings>();
        
        if (proxySettings?.Enabled == true && !string.IsNullOrWhiteSpace(proxySettings.Host) && proxySettings.Port > 0)
        {
            try
            {
                _logger.LogInformation("Настройка прокси: {Host}:{Port}", proxySettings.Host, proxySettings.Port);
                
                WebProxy proxy;
                
                // Проверяем, является ли адрес IPv6
                if (proxySettings.Host.Contains(":"))
                {
                    // Для IPv6 адресов оборачиваем в квадратные скобки
                    proxy = new WebProxy($"[{proxySettings.Host}]:{proxySettings.Port}");
                    _logger.LogInformation("Используется IPv6 прокси: {Host}", proxySettings.Host);
                }
                else
                {
                    // Для IPv4 используем стандартный формат
                    proxy = new WebProxy(proxySettings.Host, proxySettings.Port);
                    _logger.LogInformation("Используется IPv4 прокси: {Host}", proxySettings.Host);
                }
                
                // Добавляем учётные данные для аутентификации, если они указаны
                if (!string.IsNullOrWhiteSpace(proxySettings.Username) && !string.IsNullOrWhiteSpace(proxySettings.Password))
                {
                    _logger.LogInformation("Настройка аутентификации прокси для пользователя: {Username}", proxySettings.Username);
                    proxy.Credentials = new NetworkCredential(proxySettings.Username, proxySettings.Password);
                }
                
                // Применяем прокси к обработчику
                this.Proxy = proxy;
                this.UseProxy = true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ошибка при настройке прокси-сервера");
                throw;
            }
        }
        else
        {
            _logger.LogInformation("Прокси отключен или неверно настроен");
            this.UseProxy = false;
        }
    }
} 