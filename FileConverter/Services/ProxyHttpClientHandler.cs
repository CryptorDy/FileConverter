using FileConverter.Models;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Concurrent;
using System;
using System.Linq;
using System.Timers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;

namespace FileConverter.Services;

public class ProxyHttpClientHandler : HttpClientHandler, IDisposable
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
    
    // Период в минутах для автоматической проверки прокси
    private const int CHECK_PERIOD_MINUTES = 10;
    
    // Счетчик запросов
    private int _requestCounter = 0;
    
    // Семафор для синхронизации доступа к вычислению текущего прокси
    private readonly SemaphoreSlim _proxySemaphore = new SemaphoreSlim(1, 1);
    
    // Таймер для периодической проверки прокси
    private readonly System.Timers.Timer _proxyCheckTimer;
    
    public ProxyHttpClientHandler(IConfiguration configuration, ILogger<ProxyHttpClientHandler> logger)
    {
        _logger = logger;
        _configuration = configuration;
        
        // Загружаем прокси из конфигурации
        LoadProxiesFromConfiguration();
        
        // Если нет доступных прокси, отключаем использование прокси
        if (!_availableProxies.Any())
        {
            _logger.LogInformation("Прокси отключены: нет доступных прокси в конфигурации");
            this.UseProxy = false;
        }
        else
        {
            this.UseProxy = true;
            // Устанавливаем начальный прокси
            UpdateCurrentProxy();
            
            // Логируем информацию о доступных прокси
            LogProxyStatus();
        }
        
        // Настраиваем таймер для периодической проверки прокси
        _proxyCheckTimer = new System.Timers.Timer(TimeSpan.FromMinutes(CHECK_PERIOD_MINUTES).TotalMilliseconds);
        _proxyCheckTimer.Elapsed += async (s, e) => await CheckAllProxies();
        _proxyCheckTimer.AutoReset = true;
        _proxyCheckTimer.Start();
        
        _logger.LogInformation("ProxyHttpClientHandler инициализирован, таймер проверки прокси запущен с интервалом {Minutes} минут", CHECK_PERIOD_MINUTES);
    }
    
    /// <summary>
    /// Логирует текущее состояние всех прокси
    /// </summary>
    private void LogProxyStatus()
    {
        if (_proxyStates.Count == 0)
        {
            _logger.LogInformation("Нет настроенных прокси");
            return;
        }
        
        var currentKey = _currentProxy != null ? GetProxyKey(_currentProxy) : "нет";
        
        _logger.LogInformation("=== СТАТУС ПРОКСИ СЕРВЕРОВ ===");
        _logger.LogInformation("Всего прокси: {Count}", _proxyStates.Count);
        _logger.LogInformation("Текущий прокси: {Key}", currentKey);
        
        foreach (var kvp in _proxyStates)
        {
            var state = kvp.Value;
            var status = state.IsAvailable ? "Доступен" : "Недоступен";
            var current = kvp.Key == currentKey ? " (Текущий)" : "";
            
            _logger.LogInformation(
                "Прокси {Host}:{Port} - {Status}{Current}, Ошибок: {ErrorCount}, Последняя проверка: {LastChecked}",
                state.Host, 
                state.Port, 
                status, 
                current, 
                state.ErrorCount, 
                state.LastChecked.ToString("yyyy-MM-dd HH:mm:ss"));
        }
        
        _logger.LogInformation("===============================");
    }
    
    /// <summary>
    /// Проверяет все прокси на доступность
    /// </summary>
    private async Task CheckAllProxies()
    {
        _logger.LogInformation("Начало периодической проверки всех прокси серверов...");
        
        try
        {
            // Делаем копию списка, чтобы избежать проблем с конкурентным доступом
            var proxiesToCheck = _availableProxies.ToList();
            
            if (proxiesToCheck.Count == 0)
            {
                _logger.LogInformation("Нет прокси для проверки");
                return;
            }
            
            var tasks = new List<Task<(WebProxy proxy, bool isAvailable)>>();
            
            foreach (var proxy in proxiesToCheck)
            {
                tasks.Add(CheckProxyAndReturnResult(proxy));
            }
            
            var results = await Task.WhenAll(tasks);
            
            int availableCount = results.Count(r => r.isAvailable);
            
            _logger.LogInformation("Проверка прокси завершена. Доступно: {Available} из {Total}", availableCount, proxiesToCheck.Count);
            
            if (availableCount == 0 && this.UseProxy)
            {
                _logger.LogWarning("Все прокси недоступны! Отключаем использование прокси");
                await _proxySemaphore.WaitAsync();
                try
                {
                    this.UseProxy = false;
                    _currentProxy = null;
                }
                finally
                {
                    _proxySemaphore.Release();
                }
            }
            else if (availableCount > 0 && !this.UseProxy)
            {
                await _proxySemaphore.WaitAsync();
                try
                {
                    _logger.LogInformation("Найдены доступные прокси. Включаем использование прокси");
                    this.UseProxy = true;
                    UpdateCurrentProxy();
                }
                finally
                {
                    _proxySemaphore.Release();
                }
            }
            
            // Логируем общее состояние после проверки
            LogProxyStatus();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Ошибка при проверке прокси серверов");
        }
    }
    
    /// <summary>
    /// Проверяет прокси и возвращает результат вместе с экземпляром прокси
    /// </summary>
    private async Task<(WebProxy proxy, bool isAvailable)> CheckProxyAndReturnResult(WebProxy proxy)
    {
        bool isAvailable = await CheckProxyAvailability(proxy);
        return (proxy, isAvailable);
    }
    
    private void LoadProxiesFromConfiguration()
    {
        var proxySettings = _configuration.GetSection("Proxy").Get<ProxySettings>();
        
        if (proxySettings?.Enabled != true)
        {
            _logger.LogInformation("Прокси отключены в конфигурации");
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
                    _logger.LogError(ex, "Ошибка создания прокси для {Host}:{Port}", server.Host, server.Port);
                }
            }
            
            _logger.LogInformation("Загружено {Count} прокси из конфигурации", _availableProxies.Count);
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
                _logger.LogDebug("Создан IPv6 прокси: {Host}:{Port}", host, port);
            }
            else
            {
                // Для IPv4 используем стандартный формат
                proxy = new WebProxy(host, port);
                _logger.LogDebug("Создан IPv4 прокси: {Host}:{Port}", host, port);
            }
            
            // Добавляем учётные данные для аутентификации, если они указаны
            if (!string.IsNullOrWhiteSpace(username) && !string.IsNullOrWhiteSpace(password))
            {
                proxy.Credentials = new NetworkCredential(username, password);
                _logger.LogDebug("Добавлены учетные данные для прокси {Host}:{Port}", host, port);
            }
            
            return proxy;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Не удалось создать прокси для {Host}:{Port}", host, port);
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
            var previousProxy = _currentProxy;
            _currentProxy = GetNextAvailableProxy();
            
            if (_currentProxy != null)
            {
                var key = GetProxyKey(_currentProxy);
                string proxyInfo = "";
                
                if (_proxyStates.TryGetValue(key, out var state))
                {
                    proxyInfo = $"{state.Host}:{state.Port}";
                }
                else
                {
                    proxyInfo = _currentProxy.Address?.ToString() ?? "неизвестный";
                }
                
                if (previousProxy != null)
                {
                    var previousKey = GetProxyKey(previousProxy);
                    string previousProxyInfo = "";
                    
                    if (_proxyStates.TryGetValue(previousKey, out var prevState))
                    {
                        previousProxyInfo = $"{prevState.Host}:{prevState.Port}";
                    }
                    else
                    {
                        previousProxyInfo = previousProxy.Address?.ToString() ?? "неизвестный";
                    }
                    
                    _logger.LogInformation("Прокси переключен с {PreviousProxy} на {CurrentProxy}", previousProxyInfo, proxyInfo);
                }
                else
                {
                    _logger.LogInformation("Используем прокси: {CurrentProxy}", proxyInfo);
                }
                
                this.Proxy = _currentProxy;
                this.UseProxy = true;
            }
            else
            {
                _logger.LogWarning("Нет доступных прокси. Прокси отключен.");
                this.UseProxy = false;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Ошибка при обновлении текущего прокси");
            this.UseProxy = false;
        }
    }
    
    private WebProxy? GetNextAvailableProxy()
    {
        if (_availableProxies.Count == 0)
        {
            _logger.LogWarning("Нет прокси в списке доступных");
            return null;
        }
        
        // Создаем копию списка для обхода
        var proxies = _availableProxies.ToList();
        
        // Если есть текущий прокси, начинаем поиск со следующего
        int startIndex = 0;
        if (_currentProxy != null)
        {
            int currentIndex = proxies.FindIndex(p => ProxyEquals(p, _currentProxy));
            if (currentIndex >= 0)
            {
                startIndex = (currentIndex + 1) % proxies.Count;
            }
        }
        
        // Обходим список начиная со startIndex
        for (int i = 0; i < proxies.Count; i++)
        {
            int index = (startIndex + i) % proxies.Count;
            var proxy = proxies[index];
            var key = GetProxyKey(proxy);
            
            if (_proxyStates.TryGetValue(key, out var state))
            {
                // Прокси доступен или прошло достаточно времени для повторной проверки
                if (state.IsAvailable || (DateTime.UtcNow - state.LastChecked).TotalMinutes >= RETRY_PERIOD_MINUTES)
                {
                    _logger.LogDebug("Выбран прокси {Host}:{Port}, IsAvailable={IsAvailable}", state.Host, state.Port, state.IsAvailable);
                    return proxy;
                }
            }
            else
            {
                // Если состояние не найдено, считаем прокси доступным
                _logger.LogDebug("Выбран прокси {Address} (состояние не найдено)", proxy.Address);
                return proxy;
            }
        }
        
        // Если не нашли ни одного доступного, берем первый из списка
        var fallbackProxy = proxies.FirstOrDefault();
        if (fallbackProxy != null)
        {
            _logger.LogWarning("Не найдено доступных прокси, используем первый из списка: {Address}", fallbackProxy.Address);
        }
        return fallbackProxy;
    }
    
    private bool ProxyEquals(WebProxy proxy1, WebProxy proxy2)
    {
        if (proxy1 == null || proxy2 == null)
            return false;
            
        if (proxy1.Address == null || proxy2.Address == null)
            return false;
            
        return proxy1.Address.Equals(proxy2.Address);
    }
    
    /// <summary>
    /// Проверяет доступность прокси-сервера
    /// </summary>
    /// <param name="proxy">Прокси-сервер для проверки</param>
    /// <returns>true, если прокси доступен, иначе false</returns>
    private async Task<bool> CheckProxyAvailability(WebProxy proxy)
    {
        var key = GetProxyKey(proxy);
        string proxyInfo = "";
        
        if (_proxyStates.TryGetValue(key, out var state))
        {
            proxyInfo = $"{state.Host}:{state.Port}";
        }
        else
        {
            proxyInfo = proxy.Address?.ToString() ?? "неизвестный";
        }
        
        _logger.LogDebug("Проверка доступности прокси: {Proxy}...", proxyInfo);
        
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
            if (_proxyStates.TryGetValue(key, out state))
            {
                bool wasAvailable = state.IsAvailable;
                state.IsAvailable = response.IsSuccessStatusCode;
                state.LastChecked = DateTime.UtcNow;
                
                if (response.IsSuccessStatusCode)
                {
                    if (!wasAvailable)
                    {
                        _logger.LogInformation("Прокси {Proxy} снова доступен", proxyInfo);
                    }
                    state.ErrorCount = 0;
                }
                else
                {
                    _logger.LogWarning("Прокси {Proxy} не доступен. HTTP код: {StatusCode}", proxyInfo, response.StatusCode);
                }
            }
            
            // Проверяем успешность запроса
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Ошибка при проверке прокси {Proxy}: {Message}", proxyInfo, ex.Message);
            
            // Обновляем состояние прокси при ошибке
            if (_proxyStates.TryGetValue(key, out var state2))
            {
                bool wasAvailable = state2.IsAvailable;
                state2.IsAvailable = false;
                state2.LastChecked = DateTime.UtcNow;
                
                if (wasAvailable)
                {
                    _logger.LogWarning("Прокси {Proxy} помечен как недоступный", proxyInfo);
                }
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
                _logger.LogWarning("Прокси {Host}:{Port} - количество ошибок: {ErrorCount}/{Threshold}", 
                    state.Host, state.Port, state.ErrorCount, ERROR_THRESHOLD);
                
                if (state.ErrorCount >= ERROR_THRESHOLD)
                {
                    // Помечаем прокси как недоступный
                    state.IsAvailable = false;
                    state.LastChecked = DateTime.UtcNow;
                    _logger.LogWarning("Прокси {Host}:{Port} помечен как недоступный, превышен порог ошибок", 
                        state.Host, state.Port);
                    
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
        int requestId = Interlocked.Increment(ref _requestCounter);
        var url = request.RequestUri?.ToString() ?? "неизвестный URL";
        string currentProxyInfo = "прямое соединение";
        
        if (this.UseProxy && _currentProxy != null)
        {
            var key = GetProxyKey(_currentProxy);
            if (_proxyStates.TryGetValue(key, out var state))
            {
                currentProxyInfo = $"{state.Host}:{state.Port}";
            }
            else
            {
                currentProxyInfo = _currentProxy.Address?.ToString() ?? "неизвестный прокси";
            }
        }
        
        _logger.LogDebug("Запрос #{RequestId}: {Method} {Url} через {Proxy}", requestId, request.Method, url, currentProxyInfo);
        
        try
        {
            var startTime = DateTime.Now;
            var response = await base.SendAsync(request, cancellationToken);
            var elapsed = DateTime.Now - startTime;
            
            _logger.LogDebug("Запрос #{RequestId}: {StatusCode} получен за {Elapsed}мс через {Proxy}", 
                requestId, (int)response.StatusCode, elapsed.TotalMilliseconds, currentProxyInfo);
            
            return response;
        }
        catch (HttpRequestException ex) when (ex.InnerException is WebException webEx && 
                                          (webEx.Status == WebExceptionStatus.NameResolutionFailure ||
                                           webEx.Status == WebExceptionStatus.ConnectFailure ||
                                           webEx.Status == WebExceptionStatus.ProxyNameResolutionFailure ||
                                           webEx.Status == WebExceptionStatus.ConnectionClosed))
        {
            // Скорее всего проблема с прокси
            _logger.LogWarning("Запрос #{RequestId}: Ошибка соединения с прокси {Proxy}. Статус: {Status}. Переключаем на другой прокси.", 
                requestId, currentProxyInfo, webEx.Status);
            
            // Помечаем текущий прокси как проблемный
            await MarkCurrentProxyAsFailed();
            
            // Если текущий прокси был помечен как недоступный и заменен на другой, повторяем запрос
            if (this.UseProxy && this.Proxy != null)
            {
                string newProxyInfo = "прямое соединение";
                if (_currentProxy != null)
                {
                    var key = GetProxyKey(_currentProxy);
                    if (_proxyStates.TryGetValue(key, out var state))
                    {
                        newProxyInfo = $"{state.Host}:{state.Port}";
                    }
                    else
                    {
                        newProxyInfo = _currentProxy.Address?.ToString() ?? "неизвестный прокси";
                    }
                }
                
                _logger.LogInformation("Запрос #{RequestId}: Повторная попытка через новый прокси {NewProxy}", requestId, newProxyInfo);
                return await base.SendAsync(request, cancellationToken);
            }
            
            // Если нет доступных прокси, передаем исключение дальше
            _logger.LogError("Запрос #{RequestId}: Нет доступных прокси. Запрос не выполнен.", requestId);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Запрос #{RequestId}: Необработанная ошибка при выполнении запроса через {Proxy}", requestId, currentProxyInfo);
            throw;
        }
    }
    
    /// <summary>
    /// Освобождает ресурсы
    /// </summary>
    public new void Dispose()
    {
        // Останавливаем таймер
        _proxyCheckTimer?.Stop();
        _proxyCheckTimer?.Dispose();
        
        // Освобождаем семафор
        _proxySemaphore?.Dispose();
        
        // Вызываем базовый Dispose
        base.Dispose();
    }
} 