using FileConverter.Models;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using System.Text;
using System.Net.Sockets;
using System.IO;

namespace FileConverter.Services;

public class ProxyHttpClientHandler : HttpClientHandler, IDisposable
{
    private readonly ILogger<ProxyHttpClientHandler> _logger;
    private readonly ProxyPool _proxyPool;

    // Закрепленный прокси для этого экземпляра
    private ProxyServer? _assignedProxy;
    private int _assignedProxyId;

    // Счетчик запросов
    private int _requestCounter = 0;

    // Максимальное время ожидания для запросов (в секундах)
    private const int PROXY_CONNECTION_TIMEOUT_SECONDS = 30;

    public ProxyHttpClientHandler(ProxyPool proxyPool, ILogger<ProxyHttpClientHandler> logger)
    {
        _logger = logger;
        _proxyPool = proxyPool;

        _logger.LogInformation("Инициализация ProxyHttpClientHandler...");

        // Настройка ServicePoint для управления соединениями
        ConfigureServicePointManager();

        // Арендуем прокси из пула
        _assignedProxy = _proxyPool.RentAsync().GetAwaiter().GetResult();
        
        if (_assignedProxy != null)
        {
            _assignedProxyId = _assignedProxy.Id;
            this.Proxy = ProxyPool.CreateWebProxy(_assignedProxy);
            this.UseProxy = true;
            
            _logger.LogInformation("ProxyHttpClientHandler инициализирован с прокси {Host}:{Port}", 
                _assignedProxy.Host, _assignedProxy.Port);
        }
        else
        {
            this.UseProxy = false;
            _logger.LogInformation("ProxyHttpClientHandler инициализирован без прокси (нет доступных)");
        }
    }

    /// <summary>
    /// Настраивает параметры ServicePointManager для улучшения обработки соединений
    /// </summary>
    private void ConfigureServicePointManager()
    {
        try
        {
            // Максимальное время ожидания при установке соединения
            ServicePointManager.MaxServicePointIdleTime = 10000; // 10 секунд
            
            // Максимальное число одновременных соединений к одному хосту
            ServicePointManager.DefaultConnectionLimit = 20;
            
            // Увеличиваем скорость определения разрыва соединения
            ServicePointManager.DnsRefreshTimeout = 10000; // 10 секунд
            
            // Уменьшаем время ожидания установки подключения
            ServicePointManager.Expect100Continue = false;
            
            // Отключаем проверку сертификатов для работы с недоверенными прокси
            ServicePointManager.ServerCertificateValidationCallback = (sender, certificate, chain, sslPolicyErrors) => true;
            
            // Включаем поддержку всех безопасных протоколов TLS
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12 | SecurityProtocolType.Tls13;
            
            _logger.LogInformation("ServicePointManager настроен: MaxIdleTime={MaxIdleTime}мс, ConnectionLimit={ConnectionLimit}, DnsRefreshTimeout={DnsRefresh}мс", 
                ServicePointManager.MaxServicePointIdleTime,
                ServicePointManager.DefaultConnectionLimit,
                ServicePointManager.DnsRefreshTimeout);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Ошибка при настройке ServicePointManager");
        }
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

    // Переопределяем метод отправки для обработки ошибок прокси
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        int requestId = Interlocked.Increment(ref _requestCounter);
        var url = request.RequestUri?.ToString() ?? "неизвестный URL";
        string proxyInfo = _assignedProxy != null ? $"{_assignedProxy.Host}:{_assignedProxy.Port}" : "прямое соединение";

        _logger.LogDebug("Запрос #{RequestId}: {Method} {Url} через {Proxy}", requestId, request.Method, url, proxyInfo);

        try
        {
            // Создаем таймаут для автоматического прерывания запроса
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(PROXY_CONNECTION_TIMEOUT_SECONDS));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token, cancellationToken);

            var startTime = DateTime.Now;
            var response = await base.SendAsync(request, linkedCts.Token);
            var elapsed = DateTime.Now - startTime;

            // Приблизительно оцениваем размер переданных данных
            long estimatedBytes = 0;
            if (response.Content != null && response.Content.Headers.ContentLength.HasValue)
            {
                estimatedBytes = response.Content.Headers.ContentLength.Value;
            }

            _logger.LogDebug("Запрос #{RequestId}: {StatusCode} получен за {Elapsed}мс через {Proxy}, размер: {Size}",
                requestId, (int)response.StatusCode, elapsed.TotalMilliseconds, proxyInfo,
                FormatBytes(estimatedBytes));

            return response;
        }
        // Ошибки прокси
        catch (HttpRequestException ex) when (IsProxyException(ex))
        {
            string errorDetails = GetDetailedErrorMessage(ex);
            
            _logger.LogWarning("Запрос #{RequestId}: Ошибка прокси {Proxy}. Ошибка: {Error}",
                requestId, proxyInfo, errorDetails);

            // Помечаем прокси как проблемный
            if (_assignedProxy != null)
            {
                await _proxyPool.MarkAsFailedAsync(_assignedProxyId, errorDetails);
            }

            throw;
        }
        // Таймауты и отмены
        catch (TaskCanceledException ex)
        {
            bool isTimeout = IsTimeoutException(ex, cancellationToken);

            if (isTimeout)
            {
                _logger.LogWarning("Запрос #{RequestId}: Таймаут запроса через {Proxy}. Сообщение: {Message}",
                    requestId, proxyInfo, ex.Message);

                // Помечаем прокси как проблемный при таймауте
                if (_assignedProxy != null)
                {
                    await _proxyPool.MarkAsFailedAsync(_assignedProxyId, $"Timeout: {ex.Message}");
                }
            }
            else if (cancellationToken.IsCancellationRequested)
            {
                _logger.LogInformation("Запрос #{RequestId} был отменен пользователем.", requestId);
            }
            else
            {
                _logger.LogWarning("Запрос #{RequestId}: Отмена запроса через {Proxy} по неизвестной причине. Сообщение: {Message}",
                    requestId, proxyInfo, ex.Message);
            }

            throw;
        }
        // Другие ошибки
        catch (Exception ex)
        {
            _logger.LogError(ex, "Запрос #{RequestId}: Необработанная ошибка при выполнении запроса через {Proxy}",
                requestId, proxyInfo);

            // Для некоторых исключений также помечаем прокси как проблемный
            if (ShouldSwitchProxyForException(ex) && _assignedProxy != null)
            {
                _logger.LogWarning("Запрос #{RequestId}: Помечаем прокси как проблемный из-за ошибки: {Error}",
                    requestId, ex.Message);

                await _proxyPool.MarkAsFailedAsync(_assignedProxyId, ex.Message);
            }

            throw;
        }
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
    /// Определяет нужно ли помечать прокси как проблемный для данного типа исключения
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
    /// Определяет, является ли исключение результатом таймаута, а не пользовательской отмены
    /// </summary>
    private bool IsTimeoutException(TaskCanceledException ex, CancellationToken userCancellationToken)
    {
        // Проверяем содержит ли сообщение информацию о таймауте
        bool messageIndicatesTimeout = ex.Message.Contains("timeout", StringComparison.OrdinalIgnoreCase) || 
                                       ex.Message.Contains("timed out", StringComparison.OrdinalIgnoreCase);
        
        // Проверяем наличие внутреннего TimeoutException
        bool hasTimeoutInnerException = ex.InnerException is TimeoutException;
        
        // Проверяем, была ли запрошена отмена пользователем
        bool userRequestedCancellation = userCancellationToken.IsCancellationRequested;
        
        // Если пользователь запросил отмену, то это не таймаут
        if (userRequestedCancellation)
            return false;
            
        // В противном случае считаем таймаутом, если есть соответствующие признаки
        return messageIndicatesTimeout || hasTimeoutInnerException;
    }

    /// <summary>
    /// Освобождает ресурсы
    /// </summary>
    public new void Dispose()
    {
        // Возвращаем прокси в пул
        if (_assignedProxy != null)
        {
            _proxyPool.Return(_assignedProxyId);
            _logger.LogDebug("Прокси {Host}:{Port} возвращен в пул", _assignedProxy.Host, _assignedProxy.Port);
        }

        // Вызываем базовый Dispose
        base.Dispose();
    }
}