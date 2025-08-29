using FileConverter.Data;
using FileConverter.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Net;
using System.Net.Http;
using System.Timers;

namespace FileConverter.Services;

public class ProxyPoolOptions
{
    public int ReloadIntervalMinutes { get; set; } = 5;
    public int ErrorThreshold { get; set; } = 3;
    public int RetryPeriodMinutes { get; set; } = 30;
    public int MaxActiveClientsPerProxy { get; set; } = 50; // Лимит клиентов на прокси
    public int MaxConcurrentRentals { get; set; } = 100; // Максимум одновременных аренд
}

public class ProxyPool : IDisposable
{
    private readonly ILogger<ProxyPool> _logger;
    private readonly IServiceProvider _serviceProvider;
    private readonly ProxyPoolOptions _options;
    private readonly EmailNotificationService _emailService;
    private readonly System.Timers.Timer _reloadTimer;
    private readonly object _lock = new object();
    private readonly SemaphoreSlim _rentalSemaphore;
    
    // Кэш прокси в памяти для быстрого доступа
    private List<ProxyServer> _cachedProxies = new();
    private int _nextIndex = 0;
    private bool _disposed = false;

    public ProxyPool(IServiceProvider serviceProvider, ILogger<ProxyPool> logger, IOptions<ProxyPoolOptions> options, EmailNotificationService emailService)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
        _options = options.Value;
        _emailService = emailService;
        _rentalSemaphore = new SemaphoreSlim(_options.MaxConcurrentRentals, _options.MaxConcurrentRentals);
        
        // Загружаем прокси при старте
        LoadProxiesFromDatabase();
        
        // Настраиваем таймер для перезагрузки из БД
        _reloadTimer = new System.Timers.Timer(TimeSpan.FromMinutes(_options.ReloadIntervalMinutes).TotalMilliseconds);
        _reloadTimer.Elapsed += async (s, e) => 
        {
            try
            {
                LoadProxiesFromDatabase();
                await CheckCriticalSituationAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ошибка в reload таймере");
            }
        };
        _reloadTimer.AutoReset = true;
        _reloadTimer.Start();
        
        _logger.LogInformation("ProxyPool инициализирован. Перезагрузка каждые {Reload} мин, лимит клиентов на прокси: {MaxClients}", 
            _options.ReloadIntervalMinutes, _options.MaxActiveClientsPerProxy);
    }

    /// <summary>
    /// Арендует прокси для использования
    /// </summary>
    public async Task<ProxyServer?> RentAsync()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(ProxyPool));
        }

        // Ограничиваем количество одновременных аренд
        await _rentalSemaphore.WaitAsync();

        try
        {
            lock (_lock)
            {
                // Быстрая фильтрация доступных прокси
                var availableProxies = new List<ProxyServer>();
                foreach (var proxy in _cachedProxies)
                {
                    if (proxy.IsActive && proxy.IsAvailable && proxy.ActiveClients < _options.MaxActiveClientsPerProxy)
                    {
                        availableProxies.Add(proxy);
                    }
                }

                if (!availableProxies.Any())
                {
                    // Если нет доступных, пробуем взять любой активный с наименьшей нагрузкой
                    availableProxies = _cachedProxies
                        .Where(p => p.IsActive)
                        .OrderBy(p => p.ActiveClients)
                        .Take(5) // Берем только 5 с наименьшей нагрузкой
                        .ToList();
                    
                    if (!availableProxies.Any())
                    {
                        _logger.LogWarning("Нет доступных прокси для аренды");
                        return null;
                    }
                    
                    _logger.LogInformation("Нет доступных прокси, используем недоступный: {Host}:{Port}", 
                        availableProxies[0].Host, availableProxies[0].Port);
                }

                // Сброс индекса если список изменился
                if (_nextIndex >= availableProxies.Count)
                {
                    _nextIndex = 0;
                }

                // Round-robin выбор
                var selectedProxy = availableProxies[_nextIndex % availableProxies.Count];
                _nextIndex++;

                // Увеличиваем счетчик активных клиентов
                selectedProxy.ActiveClients++;
                
                _logger.LogDebug("Арендован прокси {Host}:{Port} (активных клиентов: {ActiveClients}/{MaxClients})", 
                    selectedProxy.Host, selectedProxy.Port, selectedProxy.ActiveClients, _options.MaxActiveClientsPerProxy);

                return selectedProxy;
            }
        }
        finally
        {
            _rentalSemaphore.Release();
        }
    }

    /// <summary>
    /// Синхронная версия аренды для обратной совместимости
    /// </summary>
    public ProxyServer? Rent()
    {
        return RentAsync().GetAwaiter().GetResult();
    }

    /// <summary>
    /// Возвращает прокси после использования
    /// </summary>
    public void Return(int proxyId)
    {
        if (_disposed) return;

        lock (_lock)
        {
            var proxy = _cachedProxies.FirstOrDefault(p => p.Id == proxyId);
            if (proxy != null)
            {
                proxy.ActiveClients = Math.Max(0, proxy.ActiveClients - 1);
                _logger.LogDebug("Возвращен прокси {Host}:{Port} (активных клиентов: {ActiveClients})", 
                    proxy.Host, proxy.Port, proxy.ActiveClients);
            }
        }
    }

    /// <summary>
    /// Помечает прокси как недоступный
    /// </summary>
    public async Task MarkAsFailedAsync(int proxyId, string error)
    {
        if (_disposed) return;

        ProxyServer? failedProxy = null;
        bool wasAvailable = false;

        lock (_lock)
        {
            var proxy = _cachedProxies.FirstOrDefault(p => p.Id == proxyId);
            if (proxy != null)
            {
                wasAvailable = proxy.IsAvailable;
                proxy.ErrorCount++;
                proxy.LastError = error;
                proxy.UpdatedAt = DateTime.UtcNow;

                if (proxy.ErrorCount >= _options.ErrorThreshold)
                {
                    proxy.IsAvailable = false;
                    proxy.LastChecked = DateTime.UtcNow;
                    _logger.LogWarning("Прокси {Host}:{Port} помечен как недоступный после {ErrorCount} ошибок", 
                        proxy.Host, proxy.Port, proxy.ErrorCount);
                }
                else
                {
                    _logger.LogWarning("Прокси {Host}:{Port} - ошибка #{ErrorCount}/{Threshold}: {Error}", 
                        proxy.Host, proxy.Port, proxy.ErrorCount, _options.ErrorThreshold, error);
                }

                failedProxy = proxy;
            }
        }

        // Отправляем уведомление асинхронно
        if (failedProxy != null)
        {
            await _emailService.SendProxyFailureNotificationAsync(
                failedProxy.Host, 
                failedProxy.Port, 
                error, 
                failedProxy.ErrorCount, 
                _options.ErrorThreshold);
        }
    }

    /// <summary>
    /// Синхронная версия для обратной совместимости
    /// </summary>
    public void MarkAsFailed(int proxyId, string error)
    {
        MarkAsFailedAsync(proxyId, error).GetAwaiter().GetResult();
    }

    /// <summary>
    /// Помечает прокси как доступный (для восстановления)
    /// </summary>
    public async Task MarkAsAvailableAsync(int proxyId)
    {
        if (_disposed) return;

        ProxyServer? recoveredProxy = null;

        lock (_lock)
        {
            var proxy = _cachedProxies.FirstOrDefault(p => p.Id == proxyId);
            if (proxy != null && !proxy.IsAvailable)
            {
                proxy.IsAvailable = true;
                proxy.ErrorCount = 0;
                proxy.LastError = null;
                proxy.LastChecked = DateTime.UtcNow;
                proxy.UpdatedAt = DateTime.UtcNow;
                
                _logger.LogInformation("Прокси {Host}:{Port} помечен как доступный", proxy.Host, proxy.Port);
                recoveredProxy = proxy;
            }
        }

        // Отправляем уведомление о восстановлении асинхронно
        if (recoveredProxy != null)
        {
            await _emailService.SendProxyRecoveryNotificationAsync(recoveredProxy.Host, recoveredProxy.Port);
        }
    }

    /// <summary>
    /// Синхронная версия для обратной совместимости
    /// </summary>
    public void MarkAsAvailable(int proxyId)
    {
        MarkAsAvailableAsync(proxyId).GetAwaiter().GetResult();
    }

    /// <summary>
    /// Загружает прокси из базы данных
    /// </summary>
    private void LoadProxiesFromDatabase()
    {
        try
        {
            using var scope = _serviceProvider.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            
            // Оптимизированный запрос - берем только нужные поля
            var dbProxies = dbContext.ProxyServers
                .Where(p => p.IsActive)
                .Select(p => new { p.Id, p.Host, p.Port, p.Username, p.Password, p.IsActive, p.IsAvailable, p.LastChecked, p.ErrorCount, p.LastError, p.CreatedAt, p.UpdatedAt })
                .ToList();

            lock (_lock)
            {
                // Обновляем кэш, сохраняя текущие счетчики активных клиентов
                var currentCounts = _cachedProxies.ToDictionary(p => p.Id, p => p.ActiveClients);
                
                _cachedProxies = dbProxies.Select(p => new ProxyServer
                {
                    Id = p.Id,
                    Host = p.Host,
                    Port = p.Port,
                    Username = p.Username,
                    Password = p.Password,
                    IsActive = p.IsActive,
                    IsAvailable = p.IsAvailable,
                    ActiveClients = currentCounts.GetValueOrDefault(p.Id, 0),
                    LastChecked = p.LastChecked,
                    ErrorCount = p.ErrorCount,
                    LastError = p.LastError,
                    CreatedAt = p.CreatedAt,
                    UpdatedAt = p.UpdatedAt
                }).ToList();

                // Сбрасываем индекс если список изменился
                if (_nextIndex >= _cachedProxies.Count)
                {
                    _nextIndex = 0;
                }
            }

            _logger.LogInformation("Загружено {Count} прокси из базы данных", _cachedProxies.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Ошибка при загрузке прокси из базы данных");
        }
    }

    /// <summary>
    /// Создает WebProxy из ProxyServer
    /// </summary>
    public static WebProxy CreateWebProxy(ProxyServer proxy)
    {
        var webProxy = new WebProxy(proxy.Host, proxy.Port);
        
        if (!string.IsNullOrEmpty(proxy.Username) && !string.IsNullOrEmpty(proxy.Password))
        {
            webProxy.Credentials = new NetworkCredential(proxy.Username, proxy.Password);
        }
        
        return webProxy;
    }

    /// <summary>
    /// Получает статистику пула прокси
    /// </summary>
    public (int total, int available, int active, int failed, int overloaded) GetStats()
    {
        lock (_lock)
        {
            return (
                total: _cachedProxies.Count,
                available: _cachedProxies.Count(p => p.IsActive && p.IsAvailable),
                active: _cachedProxies.Sum(p => p.ActiveClients),
                failed: _cachedProxies.Count(p => !p.IsAvailable),
                overloaded: _cachedProxies.Count(p => p.ActiveClients >= _options.MaxActiveClientsPerProxy)
            );
        }
    }

    /// <summary>
    /// Проверяет критическую ситуацию и отправляет уведомление при необходимости
    /// </summary>
    public async Task CheckCriticalSituationAsync()
    {
        var stats = GetStats();
        
        // Критическая ситуация: более 70% прокси недоступны ИЛИ менее 2 доступных прокси
        bool isCritical = false;
        string reason = "";
        
        if (stats.total > 0)
        {
            var failedPercentage = (double)stats.failed / stats.total;
            
            if (failedPercentage > 0.7)
            {
                isCritical = true;
                reason = $"Недоступно {Math.Round(failedPercentage * 100, 1)}% прокси";
            }
            else if (stats.available < 2)
            {
                isCritical = true;
                reason = $"Доступно менее 2 прокси ({stats.available})";
            }
        }
        
        if (isCritical)
        {
            _logger.LogWarning("Критическая ситуация с прокси: {Reason}. Всего: {Total}, Доступно: {Available}, Недоступно: {Failed}", 
                reason, stats.total, stats.available, stats.failed);
                
            await _emailService.SendCriticalProxyNotificationAsync(stats.total, stats.available, stats.failed);
        }
    }

    /// <summary>
    /// Освобождает ресурсы
    /// </summary>
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Защищенный метод Dispose
    /// </summary>
    protected virtual void Dispose(bool disposing)
    {
        if (_disposed) return;

        if (disposing)
        {
            _reloadTimer?.Stop();
            _reloadTimer?.Dispose();
            _rentalSemaphore?.Dispose();
        }

        _disposed = true;
    }

    /// <summary>
    /// Деструктор
    /// </summary>
    ~ProxyPool()
    {
        Dispose(false);
    }
}
