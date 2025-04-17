using FileConverter.Models;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;

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
                _logger.LogInformation("Setting proxy: {Host}:{Port}", proxySettings.Host, proxySettings.Port);
                
                WebProxy proxy;
                
                // Проверяем, является ли адрес IPv6
                if (proxySettings.Host.Contains(":"))
                {
                    // Для IPv6 адресов оборачиваем в квадратные скобки
                    proxy = new WebProxy($"[{proxySettings.Host}]:{proxySettings.Port}");
                    _logger.LogInformation("Use IPv6 proxy: {Host}", proxySettings.Host);
                }
                else
                {
                    // Для IPv4 используем стандартный формат
                    proxy = new WebProxy(proxySettings.Host, proxySettings.Port);
                    _logger.LogInformation("Use IPv4 proxy: {Host}", proxySettings.Host);
                }
                
                // Добавляем учётные данные для аутентификации, если они указаны
                if (!string.IsNullOrWhiteSpace(proxySettings.Username) && !string.IsNullOrWhiteSpace(proxySettings.Password))
                {
                    proxy.Credentials = new NetworkCredential(proxySettings.Username, proxySettings.Password);
                }
                
                // Применяем прокси к обработчику
                this.Proxy = proxy;
                this.UseProxy = true;
                
                // Проверяем доступность прокси
                if (!CheckProxyAvailability(proxy).GetAwaiter().GetResult())
                {
                    _logger.LogWarning("Proxy server is not available: {Host}:{Port}", proxySettings.Host, proxySettings.Port);
                    // При необходимости можно отключить прокси, раскомментировав строку ниже
                    // this.UseProxy = false;
                }
                else
                {
                    _logger.LogInformation("Proxy server is available and working: {Host}:{Port}", proxySettings.Host, proxySettings.Port);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error while setting up proxy server");
                throw;
            }
        }
        else
        {
            _logger.LogInformation("Proxy is disabled or incorrectly configured");
            this.UseProxy = false;
        }
    }
    
    /// <summary>
    /// Проверяет доступность прокси-сервера
    /// </summary>
    /// <param name="proxy">Прокси-сервер для проверки</param>
    /// <returns>true, если прокси доступен, иначе false</returns>
    private async Task<bool> CheckProxyAvailability(WebProxy proxy)
    {
        try
        {
            // Создаем временный HttpClientHandler для проверки
            using var handler = new HttpClientHandler
            {
                Proxy = proxy,
                UseProxy = true
            };
            
            // Создаем временный HttpClient
            using var httpClient = new HttpClient(handler);
            httpClient.Timeout = TimeSpan.FromSeconds(10); // Ограничиваем время ожидания
            
            // Отправляем запрос к надежному сайту для проверки
            var response = await httpClient.GetAsync("https://www.google.com");
            
            // Проверяем успешность запроса
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error while checking proxy availability");
            return false;
        }
    }
} 