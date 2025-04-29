using FileConverter.Models;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Concurrent;

namespace FileConverter.Services;

public class ProxyHttpClientHandler : HttpClientHandler
{
    private readonly ILogger<ProxyHttpClientHandler> _logger;
    private readonly IConfiguration _configuration;
    
    // Список доступных прокси-серверов
    private ConcurrentBag<WebProxy> _availableProxies = new ConcurrentBag<WebProxy>();
    
    // Словарь для отслеживания состояния прокси
    private ConcurrentDictionary<string, ProxyServer> _proxyStates = new ConcurrentDictionary<string, ProxyServer>();
    
    // Текущий прокси-сервер
    private WebProxy? _currentProxy;
    
    // Сколько ошибок допустимо до переключения на другой прокси
    private const int ERROR_THRESHOLD = 3;
    
    // Период в минутах, после которого неудачный прокси снова становится доступным
    private const int RETRY_PERIOD_MINUTES = 5;
    
    // Семафор для синхронизации доступа к вычислению текущего прокси
    private readonly SemaphoreSlim _proxySemaphore = new SemaphoreSlim(1, 1);
    
    public ProxyHttpClientHandler(IConfiguration configuration, ILogger<ProxyHttpClientHandler> logger)
    {
        _logger = logger;
        _configuration = configuration;
        
        // Загружаем прокси из конфигурации
        LoadProxiesFromConfiguration();
        
        // Если нет доступных прокси, отключаем использование прокси
        if (!_availableProxies.Any())
        {
            _logger.LogInformation("No available proxies. Proxy usage disabled.");
            this.UseProxy = false;
        }
        else
        {
            this.UseProxy = true;
            // Устанавливаем начальный прокси
            UpdateCurrentProxy();
        }
    }
    
    private void LoadProxiesFromConfiguration()
    {
        var proxySettings = _configuration.GetSection("Proxy").Get<ProxySettings>();
        
        if (proxySettings?.Enabled != true)
        {
            _logger.LogInformation("Proxy is disabled in configuration");
            return;
        }
        
        if (proxySettings.Servers != null && proxySettings.Servers.Count > 0)
        {
            // Используем список прокси
            foreach (var server in proxySettings.Servers)
            {
                try
                {
                    var proxy = CreateWebProxy(server.Host, server.Port, server.Username, server.Password);
                    if (proxy != null)
                    {
                        _availableProxies.Add(proxy);
                        // Сохраняем состояние прокси
                        var key = GetProxyKey(server.Host, server.Port);
                        _proxyStates.TryAdd(key, server);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error creating proxy for {Host}:{Port}", server.Host, server.Port);
                }
            }
            
            _logger.LogInformation("Loaded {Count} proxies from configuration", _availableProxies.Count);
        }
        else if (!string.IsNullOrWhiteSpace(proxySettings.Host) && proxySettings.Port > 0)
        {
            // Обратная совместимость - используем одиночный прокси
            try
            {
                var proxy = CreateWebProxy(proxySettings.Host, proxySettings.Port, proxySettings.Username, proxySettings.Password);
                if (proxy != null)
                {
                    _availableProxies.Add(proxy);
                    
                    // Добавляем в словарь состояний
                    var server = new ProxyServer
                    {
                        Host = proxySettings.Host,
                        Port = proxySettings.Port,
                        Username = proxySettings.Username,
                        Password = proxySettings.Password
                    };
                    
                    var key = GetProxyKey(proxySettings.Host, proxySettings.Port);
                    _proxyStates.TryAdd(key, server);
                    
                    _logger.LogInformation("Using legacy proxy configuration: {Host}:{Port}", proxySettings.Host, proxySettings.Port);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error creating legacy proxy");
            }
        }
    }
    
    private WebProxy? CreateWebProxy(string host, int port, string? username, string? password)
    {
        if (string.IsNullOrWhiteSpace(host) || port <= 0)
        {
            return null;
        }
        
        try
        {
            WebProxy proxy;
            
            // Проверяем, является ли адрес IPv6
            if (host.Contains(":"))
            {
                // Для IPv6 адресов оборачиваем в квадратные скобки
                proxy = new WebProxy($"[{host}]:{port}");
                _logger.LogDebug("Created IPv6 proxy: {Host}", host);
            }
            else
            {
                // Для IPv4 используем стандартный формат
                proxy = new WebProxy(host, port);
                _logger.LogDebug("Created IPv4 proxy: {Host}", host);
            }
            
            // Добавляем учётные данные для аутентификации, если они указаны
            if (!string.IsNullOrWhiteSpace(username) && !string.IsNullOrWhiteSpace(password))
            {
                proxy.Credentials = new NetworkCredential(username, password);
            }
            
            return proxy;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create proxy for {Host}:{Port}", host, port);
            return null;
        }
    }
    
    private string GetProxyKey(string host, int port)
    {
        return $"{host}:{port}";
    }
    
    private string GetProxyKey(WebProxy proxy)
    {
        var uri = proxy.Address;
        if (uri == null)
        {
            return "unknown";
        }
        
        var host = uri.Host;
        var port = uri.Port;
        return GetProxyKey(host, port);
    }
    
    private void UpdateCurrentProxy()
    {
        try
        {
            _currentProxy = GetNextAvailableProxy();
            
            if (_currentProxy != null)
            {
                _logger.LogInformation("Using proxy: {Address}", _currentProxy.Address);
                this.Proxy = _currentProxy;
                this.UseProxy = true;
            }
            else
            {
                _logger.LogWarning("No available proxies found. Disabling proxy usage.");
                this.UseProxy = false;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating current proxy");
            this.UseProxy = false;
        }
    }
    
    private WebProxy? GetNextAvailableProxy()
    {
        // Находим прокси, которые доступны или были недоступны достаточно давно для повторной проверки
        foreach (var proxy in _availableProxies)
        {
            var key = GetProxyKey(proxy);
            if (_proxyStates.TryGetValue(key, out var state))
            {
                // Прокси доступен или прошло достаточно времени для повторной проверки
                if (state.IsAvailable || (DateTime.UtcNow - state.LastChecked).TotalMinutes >= RETRY_PERIOD_MINUTES)
                {
                    return proxy;
                }
            }
            else
            {
                // Если состояние не найдено, считаем прокси доступным
                return proxy;
            }
        }
        
        // Если нет доступных прокси, попробуем взять любой
        return _availableProxies.FirstOrDefault();
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
            
            // Обновляем состояние прокси
            var key = GetProxyKey(proxy);
            if (_proxyStates.TryGetValue(key, out var state))
            {
                state.IsAvailable = response.IsSuccessStatusCode;
                state.LastChecked = DateTime.UtcNow;
                if (response.IsSuccessStatusCode)
                {
                    state.ErrorCount = 0;
                }
            }
            
            // Проверяем успешность запроса
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error while checking proxy availability");
            
            // Обновляем состояние прокси при ошибке
            var key = GetProxyKey(proxy);
            if (_proxyStates.TryGetValue(key, out var state))
            {
                state.IsAvailable = false;
                state.LastChecked = DateTime.UtcNow;
            }
            
            return false;
        }
    }
    
    /// <summary>
    /// Помечает текущий прокси как недоступный и переключается на другой
    /// </summary>
    private async Task MarkCurrentProxyAsFailed()
    {
        if (_currentProxy == null)
        {
            return;
        }
        
        // Используем семафор для предотвращения одновременного доступа к обновлению прокси
        await _proxySemaphore.WaitAsync();
        
        try
        {
            var key = GetProxyKey(_currentProxy);
            if (_proxyStates.TryGetValue(key, out var state))
            {
                state.ErrorCount++;
                _logger.LogWarning("Proxy {Address} error count: {ErrorCount}", _currentProxy.Address, state.ErrorCount);
                
                if (state.ErrorCount >= ERROR_THRESHOLD)
                {
                    // Помечаем прокси как недоступный
                    state.IsAvailable = false;
                    state.LastChecked = DateTime.UtcNow;
                    _logger.LogWarning("Proxy {Address} marked as unavailable due to error threshold", _currentProxy.Address);
                    
                    // Переключаемся на другой прокси
                    UpdateCurrentProxy();
                }
            }
        }
        finally
        {
            _proxySemaphore.Release();
        }
    }
    
    // Переопределяем метод отправки для автоматического переключения прокси при ошибках
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        try
        {
            return await base.SendAsync(request, cancellationToken);
        }
        catch (HttpRequestException ex) when (ex.InnerException is WebException webEx && 
                                          (webEx.Status == WebExceptionStatus.NameResolutionFailure ||
                                           webEx.Status == WebExceptionStatus.ConnectFailure ||
                                           webEx.Status == WebExceptionStatus.ProxyNameResolutionFailure ||
                                           webEx.Status == WebExceptionStatus.ConnectionClosed))
        {
            // Скорее всего проблема с прокси
            _logger.LogWarning(ex, "Proxy connection error. Switching to another proxy.");
            
            // Помечаем текущий прокси как проблемный
            await MarkCurrentProxyAsFailed();
            
            // Если текущий прокси был помечен как недоступный и заменен на другой, повторяем запрос
            if (this.UseProxy && this.Proxy != null)
            {
                _logger.LogInformation("Retrying request with new proxy");
                return await base.SendAsync(request, cancellationToken);
            }
            
            // Если нет доступных прокси, передаем исключение дальше
            throw;
        }
    }
} 