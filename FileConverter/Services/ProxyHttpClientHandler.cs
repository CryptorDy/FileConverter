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
using System.Text;
using System.Net.Sockets;
using System.IO;

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

    // Количество успешных запросов, после которого произойдет переключение на следующий прокси
    private const int ROTATE_AFTER_REQUESTS = 50;

    // Счетчик успешных запросов для текущего прокси
    private int _currentProxySuccessCount = 0;

    // Счетчик запросов
    private int _requestCounter = 0;

    // Статистика использования прокси
    private readonly ConcurrentDictionary<string, ProxyStats> _proxyStats = new ConcurrentDictionary<string, ProxyStats>();

    // Семафор для синхронизации доступа к вычислению текущего прокси
    private readonly SemaphoreSlim _proxySemaphore = new SemaphoreSlim(1, 1);

    // Таймер для периодической проверки прокси
    private readonly System.Timers.Timer _proxyCheckTimer;

    public ProxyHttpClientHandler(IConfiguration configuration, ILogger<ProxyHttpClientHandler> logger)
    {
        _logger = logger;
        _configuration = configuration;

        _logger.LogInformation("Инициализация ProxyHttpClientHandler...");

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
        _logger.LogInformation("Ротация прокси: каждые {Count} успешных запросов", ROTATE_AFTER_REQUESTS);
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

            // Получаем статистику использования
            string statsInfo = "";
            if (_proxyStats.TryGetValue(kvp.Key, out var stats))
            {
                statsInfo = $", Запросов: {stats.RequestCount}, Успешных: {stats.SuccessCount}, Ошибок: {stats.ErrorCount}, Байт: {FormatBytes(stats.BytesTransferred)}";
            }

            _logger.LogInformation(
                "Прокси {Host}:{Port} - {Status}{Current}, Ошибок: {ErrorCount}, Последняя проверка: {LastChecked}{Stats}",
                state.Host,
                state.Port,
                status,
                current,
                state.ErrorCount,
                state.LastChecked.ToString("yyyy-MM-dd HH:mm:ss"),
                statsInfo);
        }

        _logger.LogInformation("===============================");
    }

    /// <summary>
    /// Форматирует байты в удобочитаемый вид
    /// </summary>
    private string FormatBytes(long bytes)
    {
        string[] suffixes = { "B", "KB", "MB", "GB", "TB" };
        int counter = 0;
        decimal number = bytes;

        while (Math.Round(number / 1024) >= 1)
        {
            number = number / 1024;
            counter++;
        }

        return $"{number:n1} {suffixes[counter]}";
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
                // Сбрасываем счетчик успешных запросов для нового прокси
                _currentProxySuccessCount = 0;

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
            // Используем GetProxyKey для сравнения, так как объекты WebProxy могут быть разными экземплярами
            var currentKey = GetProxyKey(_currentProxy);
            int currentIndex = proxies.FindIndex(p => GetProxyKey(p) == currentKey);
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
                    _logger.LogDebug("Выбран прокси {Host}:{Port} как следующий доступный, IsAvailable={IsAvailable}, LastChecked={LastChecked}",
                        state.Host, state.Port, state.IsAvailable, state.LastChecked);
                    // Если прокси был недоступен, но прошел период ожидания, попробуем его использовать
                    return proxy;
                }
                else
                {
                    _logger.LogDebug("Пропускаем прокси {Host}:{Port}: Недоступен (IsAvailable={IsAvailable}) и не прошел период ожидания ({RetryMinutes} мин)",
                        state.Host, state.Port, state.IsAvailable, RETRY_PERIOD_MINUTES);
                }
            }
            else
            {
                // Если состояние не найдено, считаем прокси доступным (возможно, новый прокси добавлен динамически)
                _logger.LogDebug("Выбран прокси {Address} (состояние не найдено, считаем доступным)", proxy.Address);
                return proxy;
            }
        }

        // Если не нашли ни одного доступного, возвращаем null
        _logger.LogWarning("Не найдено доступных прокси после проверки всего списка.");
        return null; // <<< ИЗМЕНЕНО: Возвращаем null вместо первого прокси
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

    /// <summary>
    /// Обновляет статистику использования прокси
    /// </summary>
    private void UpdateProxyStats(WebProxy proxy, bool success, long bytes)
    {
        if (proxy == null) return;

        var key = GetProxyKey(proxy);

        var stats = _proxyStats.GetOrAdd(key, _ => new ProxyStats());

        stats.RequestCount++;
        if (success)
        {
            stats.SuccessCount++;
            stats.BytesTransferred += bytes;
        }
        else
        {
            stats.ErrorCount++;
        }
    }

    /// <summary>
    /// Проверяет, нужно ли ротировать прокси
    /// </summary>
    private async Task CheckProxyRotation()
    {
        if (_currentProxy == null || _currentProxySuccessCount < ROTATE_AFTER_REQUESTS)
            return;

        await _proxySemaphore.WaitAsync();
        try
        {
            // Проверяем еще раз под блокировкой
            if (_currentProxySuccessCount >= ROTATE_AFTER_REQUESTS)
            {
                var key = GetProxyKey(_currentProxy);
                _logger.LogInformation("Ротация прокси: {Key} использован для {Count} успешных запросов, переключаемся на следующий",
                    key, _currentProxySuccessCount);

                UpdateCurrentProxy();
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
        WebProxy? requestProxy = null;

        if (this.UseProxy && _currentProxy != null)
        {
            requestProxy = _currentProxy;
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
            await CheckProxyRotation();

            var startTime = DateTime.Now;
            var response = await base.SendAsync(request, cancellationToken);
            var elapsed = DateTime.Now - startTime;

            // Приблизительно оцениваем размер переданных данных
            long estimatedBytes = 0;
            if (response.Content != null)
            {
                if (response.Content.Headers.ContentLength.HasValue)
                {
                    estimatedBytes = response.Content.Headers.ContentLength.Value;
                }
            }

            // Увеличиваем счетчик успешных запросов
            if (requestProxy != null)
            {
                Interlocked.Increment(ref _currentProxySuccessCount);
                UpdateProxyStats(requestProxy, true, estimatedBytes);
            }

            _logger.LogDebug("Запрос #{RequestId}: {StatusCode} получен за {Elapsed}мс через {Proxy}, размер: {Size}",
                requestId, (int)response.StatusCode, elapsed.TotalMilliseconds, currentProxyInfo,
                FormatBytes(estimatedBytes));

            return response;
        }
        // Ошибки прокси
        catch (HttpRequestException ex) when (IsProxyException(ex))
        {
            string errorDetails = GetDetailedErrorMessage(ex);
            WebProxy? failedProxy = requestProxy; // Запоминаем прокси, на котором произошла ошибка
            string failedProxyInfo = currentProxyInfo;

            _logger.LogWarning("Запрос #{RequestId}: Ошибка прокси {Proxy}. Ошибка: {Error}. Переключаем на другой прокси.",
                requestId, failedProxyInfo, errorDetails);

            // Обновляем статистику
            if (failedProxy != null)
            {
                UpdateProxyStats(failedProxy, false, 0);
            }

            // Помечаем текущий прокси как проблемный и пытаемся переключиться
            await MarkCurrentProxyAsFailed();

            // Повторяем запрос только если удалось переключиться на ДРУГОЙ доступный прокси
            if (this.UseProxy && this.Proxy != null && _currentProxy != null && !ProxyEquals(_currentProxy, failedProxy))
            {
                string newProxyInfo = GetProxyDisplayName(_currentProxy);
                _logger.LogInformation("Запрос #{RequestId}: Повторная попытка через новый прокси {NewProxy} после ошибки", requestId, newProxyInfo);

                try
                {
                    // Важно: создаем НОВЫЙ HttpRequestMessage для повторной отправки, если это необходимо
                    // В данном случае base.SendAsync может повторно использовать тот же request, 
                    // но для надежности можно было бы клонировать request, если бы он модифицировался.
                    return await base.SendAsync(request, cancellationToken);
                }
                catch (Exception retryEx)
                {
                    _logger.LogError(retryEx, "Запрос #{RequestId}: Ошибка при повторной попытке через {NewProxy}: {Error}",
                       requestId, newProxyInfo, retryEx.Message);
                    // Если повторная попытка не удалась, передаем исключение дальше
                    throw retryEx; // Передаем исключение от повторной попытки
                }
            }
            else if (this.UseProxy && this.Proxy != null && _currentProxy != null && ProxyEquals(_currentProxy, failedProxy))
            {
                _logger.LogWarning("Запрос #{RequestId}: Не удалось переключиться на другой прокси (остался {Proxy} или доступен только он). Повторная попытка не выполняется.", requestId, failedProxyInfo);
                // Не повторяем запрос, передаем исходное исключение
                throw;
            }
            else
            {
                // Если прокси отключены или нет доступных для переключения
                _logger.LogError("Запрос #{RequestId}: Нет доступных прокси для переключения после ошибки на {Proxy}. Запрос не выполнен.", requestId, failedProxyInfo);
                // Передаем исходное исключение
                throw;
            }
        }
        // Таймауты и отмены
        catch (TaskCanceledException ex)
        {
            WebProxy? failedProxy = requestProxy; // Запоминаем прокси, на котором произошла ошибка
            string failedProxyInfo = currentProxyInfo;

            _logger.LogWarning("Запрос #{RequestId}: Таймаут или отмена запроса через {Proxy}. Сообщение: {Message}. Переключаем на другой прокси.",
                requestId, failedProxyInfo, ex.Message);

            // Обновляем статистику
            if (failedProxy != null)
            {
                UpdateProxyStats(failedProxy, false, 0);
            }

            // Помечаем текущий прокси как проблемный, только если это не отмена операции пользователем
            if (!cancellationToken.IsCancellationRequested)
            {
                await MarkCurrentProxyAsFailed();
            }
            else
            {
                _logger.LogInformation("Запрос #{RequestId} был отменен пользователем.", requestId);
                // Если отмена пользователем, не повторяем запрос
                throw; // Передаем исключение TaskCanceledException
            }

            // Повторяем запрос только если удалось переключиться на ДРУГОЙ доступный прокси и запрос не был отменен
            if (this.UseProxy && this.Proxy != null && _currentProxy != null && !ProxyEquals(_currentProxy, failedProxy) && !cancellationToken.IsCancellationRequested)
            {
                string newProxyInfo = GetProxyDisplayName(_currentProxy);

                _logger.LogInformation("Запрос #{RequestId}: Повторная попытка через новый прокси {NewProxy} после таймаута",
                    requestId, newProxyInfo);

                try
                {
                    return await base.SendAsync(request, cancellationToken);
                }
                catch (Exception retryEx)
                {
                    _logger.LogError(retryEx, "Запрос #{RequestId}: Ошибка при повторной попытке после таймаута через {NewProxy}: {Error}",
                        requestId, newProxyInfo, retryEx.Message);
                    throw retryEx; // Передаем исключение от повторной попытки
                }
            }
            else if (this.UseProxy && this.Proxy != null && _currentProxy != null && ProxyEquals(_currentProxy, failedProxy) && !cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning("Запрос #{RequestId}: Не удалось переключиться на другой прокси после таймаута (остался {Proxy} или доступен только он). Повторная попытка не выполняется.", requestId, failedProxyInfo);
                // Не повторяем запрос, передаем исходное исключение
                throw;
            }
            else if (!this.UseProxy && !cancellationToken.IsCancellationRequested)
            {
                _logger.LogError("Запрос #{RequestId}: Нет доступных прокси для переключения после таймаута на {Proxy}. Запрос не выполнен.", requestId, failedProxyInfo);
                // Передаем исходное исключение
                throw;
            }
            else
            {
                // Если запрос был отменен, просто передаем исключение
                throw;
            }
        }
        // Другие ошибки
        catch (Exception ex)
        {
            _logger.LogError(ex, "Запрос #{RequestId}: Необработанная ошибка при выполнении запроса через {Proxy}",
                requestId, currentProxyInfo);

            // Обновляем статистику
            if (requestProxy != null)
            {
                UpdateProxyStats(requestProxy, false, 0);
            }

            // Для некоторых исключений также имеет смысл переключить прокси
            if (ShouldSwitchProxyForException(ex))
            {
                _logger.LogWarning("Запрос #{RequestId}: Переключаем прокси из-за ошибки: {Error}",
                    requestId, ex.Message);

                await MarkCurrentProxyAsFailed();
            }

            throw;
        }
    }

    /// <summary>
    /// Получает удобное для отображения имя прокси
    /// </summary>
    private string GetProxyDisplayName(WebProxy proxy)
    {
        if (proxy == null)
            return "прямое соединение";

        var key = GetProxyKey(proxy);
        if (_proxyStates.TryGetValue(key, out var state))
        {
            return $"{state.Host}:{state.Port}";
        }

        return proxy.Address?.ToString() ?? "неизвестный прокси";
    }

    /// <summary>
    /// Проверяет, связано ли исключение с ошибкой прокси
    /// </summary>
    private bool IsProxyException(Exception ex)
    {
        // Проверяем внутреннее исключение WebException
        if (ex.InnerException is WebException webEx)
        {
            if (webEx.Status == WebExceptionStatus.NameResolutionFailure ||
                webEx.Status == WebExceptionStatus.ConnectFailure ||
                webEx.Status == WebExceptionStatus.ProxyNameResolutionFailure ||
                webEx.Status == WebExceptionStatus.ConnectionClosed ||
                webEx.Status == WebExceptionStatus.Timeout ||
                webEx.Status == WebExceptionStatus.RequestCanceled)
            {
                return true;
            }
        }

        // Проверяем сообщение на наличие ключевых слов, связанных с прокси
        string message = ex.Message.ToLowerInvariant();
        if (message.Contains("proxy") || message.Contains("tunnel") ||
            message.Contains("502") || message.Contains("503") ||
            message.Contains("504") || message.Contains("407") ||
            message.Contains("connection refused") ||
            message.Contains("connection failed") ||
            message.Contains("unable to connect"))
        {
            return true;
        }

        return false;
    }

    /// <summary>
    /// Получает детальное сообщение об ошибке из исключения
    /// </summary>
    private string GetDetailedErrorMessage(Exception ex)
    {
        StringBuilder sb = new StringBuilder();
        sb.Append(ex.Message);

        if (ex.InnerException != null)
        {
            sb.Append(" -> ");
            sb.Append(ex.InnerException.Message);

            if (ex.InnerException is WebException webEx)
            {
                sb.Append($" (Статус: {webEx.Status})");
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// Определяет нужно ли переключать прокси для данного типа исключения
    /// </summary>
    private bool ShouldSwitchProxyForException(Exception ex)
    {
        // Проверяем на SocketException
        if (ex is SocketException || ex.InnerException is SocketException)
            return true;

        // Проверяем на IOException с определенными сообщениями
        if (ex is IOException || ex.InnerException is IOException)
        {
            string message = ex.Message;
            if (ex.InnerException != null)
                message += ex.InnerException.Message;

            message = message.ToLowerInvariant();

            if (message.Contains("connection") ||
                message.Contains("reset") ||
                message.Contains("aborted") ||
                message.Contains("closed") ||
                message.Contains("read"))
            {
                return true;
            }
        }

        return false;
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