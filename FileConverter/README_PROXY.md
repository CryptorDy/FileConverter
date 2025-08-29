# Система управления прокси

## Обзор

Новая система управления прокси решает проблему `InvalidOperationException` при изменении свойств `HttpClientHandler` после начала работы. Теперь каждый экземпляр `ProxyHttpClientHandler` получает закрепленный прокси при создании и больше его не меняет.

## Архитектура

### Компоненты

1. **ProxyServer** - модель БД для хранения информации о прокси
2. **ProxyPool** - singleton сервис для управления пулом прокси
3. **ProxyHttpClientHandler** - упрощенный обработчик HTTP с закрепленным прокси

### Принцип работы

- **ProxyPool** загружает прокси из БД и управляет их состоянием
- При создании `ProxyHttpClientHandler` вызывается `pool.Rent()` для получения прокси
- Прокси закрепляется за хэндлером и не меняется до его уничтожения
- При ошибках прокси помечается как недоступный в пуле
- Health-чеки автоматически восстанавливают доступность прокси

## Установка

### 1. Применить миграцию БД

```bash
dotnet ef database update
```

### 2. Добавить прокси в БД

Выполните SQL скрипт `Scripts/add_proxies.sql` или добавьте прокси вручную:

```sql
INSERT INTO "ProxyServers" ("Host", "Port", "Username", "Password", "IsActive", "IsAvailable", "ActiveClients", "LastChecked", "ErrorCount", "LastError", "CreatedAt", "UpdatedAt")
VALUES ('185.77.222.19', 8080, NULL, NULL, true, true, 0, NOW(), 0, NULL, NOW(), NOW());
```

### 3. Настройка

Система автоматически подхватывает прокси из БД. Для отключения прокси установите `IsActive = false`.

## Использование

### Автоматическое использование

Все HTTP клиенты в приложении автоматически используют новую систему:

- `video-downloader`
- `instagram-downloader` 
- `youtube-downloader`
- `default`

### Мониторинг

Логи показывают:
- Аренду/возврат прокси
- Ошибки и их причины
- Health-чеки и восстановление прокси
- Статистику использования

## Преимущества

1. **Исключает ошибки** - нет изменения свойств после начала работы
2. **Простота** - round-robin распределение нагрузки
3. **Надежность** - автоматическое исключение проблемных прокси
4. **Гибкость** - управление через БД без перезапуска
5. **Мониторинг** - полная видимость состояния прокси

## Конфигурация

### Настройки в appsettings.json

```json
{
  "ProxyPool": {
    "ReloadIntervalMinutes": 5,
    "ErrorThreshold": 3,
    "RetryPeriodMinutes": 30,
    "MaxActiveClientsPerProxy": 50,
    "MaxConcurrentRentals": 100
  },
  "EmailNotifications": {
    "SmtpServer": "smtp.gmail.com",
    "SmtpPort": 587,
    "SmtpUsername": "your-email@gmail.com",
    "SmtpPassword": "your-app-password",
    "FromEmail": "your-email@gmail.com",
    "FromName": "FileConverter Proxy Monitor",
    "AdminEmail": "admin@example.com",
    "EnableSsl": true,
    "EnableNotifications": true
  }
}
```

### Параметры ProxyPool

- **ReloadIntervalMinutes** - интервал перезагрузки прокси из БД (по умолчанию: 5 минут)
- **ErrorThreshold** - количество ошибок до пометки прокси как недоступного (по умолчанию: 3)
- **RetryPeriodMinutes** - период ожидания перед повторной проверкой недоступного прокси (по умолчанию: 30 минут)
- **MaxActiveClientsPerProxy** - максимальное количество активных клиентов на прокси (по умолчанию: 50)
- **MaxConcurrentRentals** - максимальное количество одновременных аренд прокси (по умолчанию: 100)

### Параметры EmailNotifications

- **SmtpServer** - SMTP сервер для отправки email (по умолчанию: smtp.gmail.com)
- **SmtpPort** - порт SMTP сервера (по умолчанию: 587)
- **SmtpUsername** - имя пользователя SMTP
- **SmtpPassword** - пароль SMTP (для Gmail используйте App Password)
- **FromEmail** - email отправителя
- **FromName** - имя отправителя
- **AdminEmail** - email администратора для уведомлений
- **EnableSsl** - включить SSL/TLS (по умолчанию: true)
- **EnableNotifications** - включить email уведомления (по умолчанию: true)
- **MaxFailureNotificationsPerHour** - максимальное количество уведомлений об ошибках в час (по умолчанию: 10)
- **MaxRecoveryNotificationsPerHour** - максимальное количество уведомлений о восстановлении в час (по умолчанию: 5)
- **MaxCriticalNotificationsPerHour** - максимальное количество критических уведомлений в час (по умолчанию: 3)
- **NotificationCooldownMinutes** - минимальный интервал между уведомлениями в минутах (по умолчанию: 30)

### Health-чеки

Система не проводит автоматические health-чеки. Проверка доступности прокси происходит через запросы клиентов:
- При ошибке прокси помечается как проблемный
- После периода ожидания прокси снова становится доступным для выбора

## Email уведомления

### Типы уведомлений

1. **Проблема с прокси** - отправляется при каждой ошибке прокси
   - Содержит информацию о прокси, ошибке и количестве ошибок
   - Предупреждает о приближении к порогу отключения

2. **Восстановление прокси** - отправляется при восстановлении недоступного прокси
   - Уведомляет о том, что прокси снова доступен

3. **Критическая ситуация** - отправляется при недоступности более 70% прокси ИЛИ менее 2 доступных прокси
   - Требует немедленного вмешательства администратора

### Настройка Gmail

Для использования Gmail в качестве SMTP сервера:

1. Включите двухфакторную аутентификацию
2. Создайте App Password в настройках безопасности
3. Используйте App Password вместо обычного пароля

```json
{
  "EmailNotifications": {
    "SmtpServer": "smtp.gmail.com",
    "SmtpPort": 587,
    "SmtpUsername": "your-email@gmail.com",
    "SmtpPassword": "your-16-digit-app-password",
    "FromEmail": "your-email@gmail.com",
    "AdminEmail": "admin@yourcompany.com",
    "EnableSsl": true,
    "EnableNotifications": true
  }
}
```

### Защита от спама

Система автоматически защищает от спама уведомлений:

- **Ограничение частоты** - максимальное количество уведомлений в час
- **Минимальный интервал** - 30 минут между уведомлениями одного типа
- **Автосброс счетчиков** - каждый час счетчики сбрасываются
- **Логирование пропусков** - в логах видно, когда уведомления пропускаются

### Отключение уведомлений

Установите `EnableNotifications: false` для отключения email уведомлений:

```json
{
  "EmailNotifications": {
    "EnableNotifications": false
  }
}
```

### Тестирование соединения

```csharp
// В контроллере или сервисе
public async Task<IActionResult> TestEmailConnection()
{
    var isConnected = await _emailService.TestConnectionAsync();
    return Ok(new { success = isConnected });
}

public IActionResult GetEmailStats()
{
    var stats = _emailService.GetNotificationStats();
    return Ok(stats);
}
```

### Пример использования

```csharp
// В контроллере или сервисе
public class ProxyMonitoringController : ControllerBase
{
    private readonly ProxyPool _proxyPool;
    private readonly EmailNotificationService _emailService;
    
    public async Task<IActionResult> GetProxyStats()
    {
        var stats = _proxyPool.GetStats();
        
        // Проверяем критическую ситуацию
        await _proxyPool.CheckCriticalSituationAsync();
        
        return Ok(new
        {
            total = stats.total,
            available = stats.available,
            failed = stats.failed,
            overloaded = stats.overloaded,
            healthPercentage = stats.total > 0 ? (double)stats.available / stats.total * 100 : 0
        });
    }
    
    public async Task<IActionResult> TestEmailNotification()
    {
        // Тестовое уведомление
        await _emailService.SendProxyFailureNotificationAsync(
            "test-proxy.example.com", 
            8080, 
            "Test error message", 
            2, 
            3);
            
        return Ok("Test notification sent");
    }
}
```

## Устранение неполадок

### Прокси недоступен

1. Проверьте логи на ошибки подключения
2. Убедитесь, что прокси активен в БД (`IsActive = true`)
3. Дождитесь health-чека или перезапустите приложение

### Все прокси недоступны

1. Проверьте настройки прокси в БД
2. Убедитесь, что хотя бы один прокси `IsActive = true`
3. Проверьте сетевую доступность прокси

### Высокая нагрузка на один прокси

Система автоматически распределяет нагрузку по round-robin. Если нужно ограничить нагрузку на прокси, добавьте логику в `ProxyPool.Rent()`.

## Масштабирование при увеличении нагрузки

### Мониторинг нагрузки

Используйте метод `GetStats()` для отслеживания состояния пула:

```csharp
var stats = proxyPool.GetStats();
// stats.total - общее количество прокси
// stats.available - доступные прокси
// stats.active - активные клиенты
// stats.failed - недоступные прокси
// stats.overloaded - перегруженные прокси (>= MaxActiveClientsPerProxy)
```

### Настройки для высокой нагрузки

#### 1. Увеличение лимитов в appsettings.json

```json
{
  "ProxyPool": {
    "MaxActiveClientsPerProxy": 100,    // Было 50
    "MaxConcurrentRentals": 200,        // Было 100
    "ReloadIntervalMinutes": 2,         // Было 5 (чаще обновляем)
    "ErrorThreshold": 5,                // Было 3 (больше попыток)
    "RetryPeriodMinutes": 15            // Было 30 (быстрее восстановление)
  }
}
```

#### 2. Добавление прокси в БД

```sql
INSERT INTO "ProxyServers" ("Host", "Port", "Username", "Password", "IsActive", "IsAvailable", "ActiveClients", "LastChecked", "ErrorCount", "LastError", "CreatedAt", "UpdatedAt")
VALUES 
    ('proxy1.example.com', 8080, 'user1', 'pass1', true, true, 0, NOW(), 0, NULL, NOW(), NOW()),
    ('proxy2.example.com', 8080, 'user2', 'pass2', true, true, 0, NOW(), 0, NULL, NOW(), NOW());
```

#### 3. Уменьшение времени жизни HTTP клиентов

В `Program.cs` для более частой ротации:

```csharp
builder.Services.AddHttpClient("video-downloader", client =>
{
    client.Timeout = TimeSpan.FromMinutes(30);
})
.ConfigurePrimaryHttpMessageHandler<ProxyHttpClientHandler>()
.SetHandlerLifetime(TimeSpan.FromMinutes(1)); // Быстрая ротация
```

### Рекомендации по нагрузке

#### До 2000 запросов/мин (33 запроса/сек)
- **10-30 прокси** - оптимально
- **MaxActiveClientsPerProxy: 50** - достаточно
- **MaxConcurrentRentals: 100** - достаточно

#### 2000-5000 запросов/мин (33-83 запроса/сек)
- **30-50 прокси** - рекомендуется
- **MaxActiveClientsPerProxy: 100** - увеличить
- **MaxConcurrentRentals: 200** - увеличить
- **ReloadIntervalMinutes: 2** - чаще обновлять

#### 5000+ запросов/мин (83+ запросов/сек)
- **50+ прокси** - обязательно
- **MaxActiveClientsPerProxy: 150** - высокий лимит
- **MaxConcurrentRentals: 500** - высокий лимит
- **Рассмотреть горизонтальное масштабирование** - несколько экземпляров приложения

### Признаки необходимости масштабирования

1. **Высокое значение `stats.overloaded`** - много перегруженных прокси
2. **`stats.active` близко к `stats.total * MaxActiveClientsPerProxy`** - все прокси загружены
3. **Медленная аренда прокси** - `RentAsync()` выполняется долго
4. **Ошибки `TimeoutException`** в логах
5. **Высокое потребление CPU** процессом

### Оптимизация производительности

#### 1. Мониторинг через логи
```csharp
// В логах ищите:
// "Арендован прокси" - время аренды
// "Возвращен прокси" - время использования
// "Нет доступных прокси" - проблемы с доступностью
```

#### 2. Настройка логирования
```json
{
  "Logging": {
    "LogLevel": {
      "FileConverter.Services.ProxyPool": "Information",
      "FileConverter.Services.ProxyHttpClientHandler": "Warning"
    }
  }
}
```

#### 3. Алерты при проблемах
Настройте мониторинг на:
- Количество недоступных прокси > 50%
- Время аренды прокси > 1 секунды
- Количество перегруженных прокси > 30%

### Практические примеры масштабирования

#### Пример 1: Увеличение с 1000 до 3000 запросов/мин

**Исходная конфигурация:**
```json
{
  "ProxyPool": {
    "MaxActiveClientsPerProxy": 50,
    "MaxConcurrentRentals": 100
  }
}
```

**Новая конфигурация:**
```json
{
  "ProxyPool": {
    "MaxActiveClientsPerProxy": 80,
    "MaxConcurrentRentals": 150,
    "ReloadIntervalMinutes": 3
  }
}
```

**Действия:**
1. Добавить 10-15 новых прокси в БД
2. Обновить конфигурацию
3. Перезапустить приложение

#### Пример 2: Критическая нагрузка 5000+ запросов/мин

**Конфигурация:**
```json
{
  "ProxyPool": {
    "MaxActiveClientsPerProxy": 120,
    "MaxConcurrentRentals": 300,
    "ReloadIntervalMinutes": 1,
    "ErrorThreshold": 7,
    "RetryPeriodMinutes": 10
  }
}
```

**Действия:**
1. Добавить 30+ прокси в БД
2. Настроить быструю ротацию HTTP клиентов
3. Рассмотреть горизонтальное масштабирование

#### Пример 3: Мониторинг через код

```csharp
// В контроллере или сервисе
public class ProxyMonitoringService
{
    private readonly ProxyPool _proxyPool;
    
    public async Task<object> GetProxyStats()
    {
        var stats = _proxyPool.GetStats();
        
        return new
        {
            total = stats.total,
            available = stats.available,
            active = stats.active,
            failed = stats.failed,
            overloaded = stats.overloaded,
            utilization = stats.total > 0 ? (double)stats.active / (stats.total * 50) * 100 : 0,
            health = stats.total > 0 ? (double)stats.available / stats.total * 100 : 0
        };
    }
}
```

## Миграция с старой системы

1. Старая система использовала конфигурацию из `appsettings.json`
2. Новая система использует БД
3. Перенесите прокси из конфигурации в БД
4. Удалите секцию `Proxy` из `appsettings.json`
