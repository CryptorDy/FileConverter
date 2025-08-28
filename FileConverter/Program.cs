using FileConverter.Data;
using FileConverter.Services;
using FileConverter.Middleware;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using System.Threading.RateLimiting;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Caching.Distributed;
using Serilog;
using System.Text;
using Amazon.S3;
using FileConverter.Services.Interfaces;
using FileConverter.BackgroundServices;
using FileConverter.Services.BackgroundServices;
using FileConverter.Helpers;
// Регистрируем кодировку Windows-1251
Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
var encoding = Encoding.GetEncoding(1251);

var builder = WebApplication.CreateBuilder(args);

// Настройка Serilog через appsettings.json
builder.Host.UseSerilog((context, services, configuration) => configuration
    .ReadFrom.Configuration(context.Configuration)
    .ReadFrom.Services(services)
    .Enrich.FromLogContext()
    .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
    .WriteTo.File(
        $"Logs/FileConverter-All-.log",
        Serilog.Events.LogEventLevel.Information,
        outputTemplate: "[{Timestamp:yyyy-MM-dd HH:mm:ss.fff}] {Level:u3} {Message:lj}{NewLine}{Exception}",
        rollingInterval: Serilog.RollingInterval.Day,
        retainedFileCountLimit: 30,
        encoding: encoding)
    .WriteTo.File(
        $"Logs/FileConverter-Errors-.log",
        Serilog.Events.LogEventLevel.Error,
        outputTemplate: "[{Timestamp:yyyy-MM-dd HH:mm:ss.fff}] {Level:u3} {Message:lj}{NewLine}{Exception}",
        rollingInterval: Serilog.RollingInterval.Day,
        retainedFileCountLimit: 30,
        encoding: encoding));

// Регистрируем обработчик HTTP запросов с прокси
builder.Services.AddTransient<ProxyHttpClientHandler>();

// Настройка HTTP клиента
builder.Services.AddHttpClient("video-downloader", client =>
{
    client.Timeout = TimeSpan.FromMinutes(
        builder.Configuration.GetValue<int>("Performance:DownloadTimeoutMinutes", 30));
})
.ConfigurePrimaryHttpMessageHandler<ProxyHttpClientHandler>();

// Добавляем именованный HTTP клиент с прокси для Instagram
builder.Services.AddHttpClient("instagram-downloader", client =>
{
    client.Timeout = TimeSpan.FromMinutes(
        builder.Configuration.GetValue<int>("Performance:DownloadTimeoutMinutes", 30));
    
    // Добавляем стандартные заголовки для обхода ограничений Instagram
    client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/123.0.0.0 Safari/537.36");
    client.DefaultRequestHeaders.Add("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,image/apng,*/*;q=0.8");
    client.DefaultRequestHeaders.Add("Accept-Language", "en-US,en;q=0.9,ru;q=0.8");
    client.DefaultRequestHeaders.Add("Referer", "https://www.instagram.com/");
    client.DefaultRequestHeaders.Add("sec-ch-ua", "\"Google Chrome\";v=\"123\", \"Not:A-Brand\";v=\"8\", \"Chromium\";v=\"123\"");
    client.DefaultRequestHeaders.Add("sec-ch-ua-mobile", "?0");
    client.DefaultRequestHeaders.Add("sec-ch-ua-platform", "\"Windows\"");
})
.ConfigurePrimaryHttpMessageHandler<ProxyHttpClientHandler>();

// Добавляем именованный HTTP клиент с прокси для YouTube
builder.Services.AddHttpClient("youtube-downloader", client =>
{
    client.Timeout = TimeSpan.FromMinutes(
        builder.Configuration.GetValue<int>("Performance:DownloadTimeoutMinutes", 30));
    
    // Добавляем стандартные заголовки для обхода блокировок YouTube
    client.DefaultRequestHeaders.Add("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,image/apng,*/*;q=0.8");
    client.DefaultRequestHeaders.Add("Accept-Language", "en-US,en;q=0.9");
    client.DefaultRequestHeaders.Add("Accept-Encoding", "gzip, deflate");
    client.DefaultRequestHeaders.Add("sec-ch-ua", "\"Google Chrome\";v=\"120\", \"Not:A-Brand\";v=\"8\", \"Chromium\";v=\"120\"");
    client.DefaultRequestHeaders.Add("sec-ch-ua-mobile", "?0");
    client.DefaultRequestHeaders.Add("sec-ch-ua-platform", "\"Windows\"");
})
.ConfigurePrimaryHttpMessageHandler<ProxyHttpClientHandler>();

// Настраиваем базовый HttpClient по умолчанию с поддержкой прокси
builder.Services.AddHttpClient("default", client => { })
    .ConfigurePrimaryHttpMessageHandler<ProxyHttpClientHandler>();
builder.Services.AddHttpClient();

// Конфигурация для высоких нагрузок
builder.WebHost.ConfigureKestrel(options =>
{
    // Увеличиваем максимальный размер тела запроса до 100 МБ
    options.Limits.MaxRequestBodySize = 104857600; // 100 MB
    
    // Увеличиваем время ожидания для долгих операций
    options.Limits.KeepAliveTimeout = TimeSpan.FromMinutes(10);
    options.Limits.RequestHeadersTimeout = TimeSpan.FromMinutes(5);
});

// Явное указание URL закомментировано, чтобы использовалась переменная окружения ASPNETCORE_URLS
builder.WebHost.UseUrls("http://0.0.0.0:5080");

// Настройка контекста базы данных с использованием настроенного dataSource
builder.Services.AddDbContext<AppDbContext>((serviceProvider, options) =>
{
    var dataSource = serviceProvider.GetRequiredService<Npgsql.NpgsqlDataSource>();
    options.UseNpgsql(dataSource, npgsqlOptions => 
        {
            npgsqlOptions.EnableRetryOnFailure(3);
            npgsqlOptions.CommandTimeout(60); // Увеличиваем таймаут команды до 60 сек
        })
        .EnableSensitiveDataLogging(false) // Отключаем логирование чувствительных данных в продакшене
        .ConfigureWarnings(warnings => warnings.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.CoreEventId.NavigationBaseIncludeIgnored));
}, ServiceLifetime.Transient); // Изменяем срок жизни контекста для предотвращения проблем с уничтоженным контекстом

// Настройка JSON сериализации для PostgreSQL
AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", true);

// Создаем и настраиваем NpgsqlDataSource с поддержкой динамического JSON
var connectionString = builder.Configuration.GetConnectionString("DefaultConnection");
var dataSourceBuilder = new Npgsql.NpgsqlDataSourceBuilder(connectionString);
dataSourceBuilder.EnableDynamicJson();
var dataSource = dataSourceBuilder.Build();

// Регистрируем dataSource как singleton
builder.Services.AddSingleton(dataSource);

// Добавляем фабрику контекста базы данных
builder.Services.AddSingleton<DbContextFactory>();

// Add services to the container.
builder.Services.AddControllers()
    .AddJsonOptions(options => 
    {
        // Настраиваем сериализацию enum как строк
        options.JsonSerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
    });
// Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// Добавляем кэширование
// Используем кэширование в памяти с продвинутыми настройками
builder.Services.AddMemoryCache(options =>
{
    // Лимит на размер кэша (в записях)
    options.SizeLimit = builder.Configuration.GetValue<long>("Caching:MemoryCacheSize", 2000);
    // Сканирование для удаления просроченных записей
    options.ExpirationScanFrequency = TimeSpan.FromMinutes(
        builder.Configuration.GetValue<double>("Caching:ExpirationScanFrequencyMinutes", 30));
});

// Настраиваем адаптер для совместимости с интерфейсом распределенного кэша
builder.Services.AddSingleton<IDistributedCache, MemoryCacheAdapter>();


// Регистрируем репозиторий и сервисы
builder.Services.AddScoped<IJobRepository, JobRepository>();
builder.Services.AddScoped<IMediaItemRepository, MediaItemRepository>();
builder.Services.AddScoped<IConversionLogRepository, ConversionLogRepository>();
builder.Services.AddScoped<IS3StorageService, S3StorageService>();
builder.Services.AddScoped<IJobManager, DbJobManager>();
builder.Services.AddScoped<IVideoConverter, VideoConverter>();
builder.Services.AddScoped<IConversionLogger, ConversionLogger>();
builder.Services.AddScoped<IJobRecoveryService, JobRecoveryService>();
builder.Services.AddSingleton<IYoutubeDownloadService, YoutubeDownloadService>();
builder.Services.AddSingleton<ProcessingChannels>();

// Сервис анализа аудио Essentia
builder.Services.AddScoped<AudioAnalyzer>();

// Кэширование, каналы и временные файлы
builder.Services.AddSingleton<DistributedCacheManager>();
builder.Services.AddSingleton<ITempFileManager, TempFileManager>();
builder.Services.AddSingleton<UrlValidator>();

// Метрики и мониторинг
builder.Services.AddSingleton<MetricsCollector>();

// Ограничение скорости запросов API
builder.Services.AddRateLimiter(options =>
{
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? httpContext.Request.Headers.Host.ToString(),
            factory: partition => new FixedWindowRateLimiterOptions
            {
                AutoReplenishment = true,
                PermitLimit = 100, // Максимум 100 запросов
                Window = TimeSpan.FromMinutes(1) // В течение 1 минуты
            }));
    
    options.OnRejected = async (context, token) =>
    {
        context.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        context.HttpContext.Response.ContentType = "application/json";
        
        await context.HttpContext.Response.WriteAsync(
            """{"error":"Too many requests. Please try again later."}""", 
            cancellationToken: token);
    };
});

// Добавляем HealthChecks для мониторинга состояния приложения
builder.Services.AddHealthChecks()
    .AddCheck("Database", () => 
    {
        try
        {
            using var scope = builder.Services.BuildServiceProvider().CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            dbContext.Database.CanConnect();
            return HealthCheckResult.Healthy("База данных доступна");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("Проблема с подключением к базе данных", ex);
        }
    }, tags: new[] { "database", "ready" })
    .AddCheck("TempStorage", () =>
    {
        try
        {
            using var scope = builder.Services.BuildServiceProvider().CreateScope();
            var tempManager = scope.ServiceProvider.GetRequiredService<ITempFileManager>();
            var tempDir = tempManager.GetTempDirectory();
            
            if (!Directory.Exists(tempDir))
            {
                return HealthCheckResult.Degraded($"Директория временных файлов не существует: {tempDir}");
            }
            
            // Проверяем, можно ли создать файл
            var testFile = Path.Combine(tempDir, "healthcheck.tmp");
            File.WriteAllText(testFile, "test");
            File.Delete(testFile);
            
            return HealthCheckResult.Healthy("Хранилище временных файлов доступно");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("Проблема с хранилищем временных файлов", ex);
        }
    }, tags: new[] { "storage", "ready" });

// Настройка параметров приложения
builder.Services.Configure<AppSettings>(builder.Configuration.GetSection("AppSettings"));

// Настройка безопасного CORS
var corsOrigins = builder.Configuration.GetSection("CORS:AllowedOrigins").Get<string[]>() ?? Array.Empty<string>();
builder.Services.AddCors(options =>
{
    if (corsOrigins.Length > 0)
    {
        // В продакшене используем ограниченный список доменов
        options.AddPolicy("SecureCorsPolicy", builderPolicy =>
        {
            builderPolicy
                .WithOrigins(corsOrigins)
                .WithMethods("GET", "POST", "OPTIONS")
                .WithHeaders("Content-Type", "Authorization")
                .AllowCredentials();
        });
    }
    else
    {
        // В разработке более либеральная политика
        options.AddPolicy("SecureCorsPolicy", builderPolicy =>
        {
            builderPolicy
                .AllowAnyOrigin()
                .WithMethods("GET", "POST", "OPTIONS")
                .WithHeaders("Content-Type", "Authorization");
        });
    }
});

// Регистрируем фоновые службы (Hosted Services)
builder.Services.AddHostedService<DownloadBackgroundService>();
builder.Services.AddHostedService<ConversionBackgroundService>();
builder.Services.AddHostedService<AudioAnalysisBackgroundService>();
builder.Services.AddHostedService<KeyframeExtractionBackgroundService>();
builder.Services.AddHostedService<UploadBackgroundService>();
builder.Services.AddHostedService<YoutubeBackgroundService>();
builder.Services.AddHostedService<JobRecoveryHostedService>();
builder.Services.AddHostedService<TempFileCleanupHostedService>();

var app = builder.Build();

// Конфигурируем ServiceActivator для доступа к DI из статических методов
ServiceActivator.Configure(app.Services);

// Миграция базы данных при запуске
// Применяем миграции во всех окружениях
using (var scope = app.Services.CreateScope())
{
    try
    {
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        app.Logger.LogInformation("Starting database migration...");
        dbContext.Database.Migrate();
        app.Logger.LogInformation("Database migration completed successfully");
    }
    catch (Exception ex)
    {
        app.Logger.LogError(ex, "Error during database migration");
        // В случае критических ошибок можно прервать запуск приложения
        // throw;
    }
}

// Глобальная обработка исключений
app.UseGlobalExceptionHandler();

// Профилирование HTTP запросов
app.UseRequestProfiling();

// Используем Rate Limiter
app.UseRateLimiter();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();

}

app.UseHttpsRedirection();
app.UseStaticFiles(); // Для доступа к файлам в wwwroot

app.UseCors("SecureCorsPolicy"); // Используем безопасную CORS политику

app.UseAuthorization();

app.MapControllers();

// Добавляем эндпоинт для проверки статуса
app.MapHealthChecks("/health", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
{
    ResponseWriter = async (context, report) =>
    {
        context.Response.ContentType = "application/json";
        var result = new
        {
            status = report.Status.ToString(),
            checks = report.Entries.Select(e => new 
            {
                name = e.Key,
                status = e.Value.Status.ToString(),
                description = e.Value.Description,
                duration = e.Value.Duration.TotalMilliseconds
            }),
            totalDuration = report.TotalDuration.TotalMilliseconds
        };
        await context.Response.WriteAsJsonAsync(result);
    }
});

// Логируем запуск приложения
Log.Information("FileConverter application started in {Environment} environment", 
    app.Environment.EnvironmentName);

try
{
    app.Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "FileConverter application terminated unexpectedly");
}
finally
{
    // Очищаем ресурсы
    try
    {
        dataSource?.Dispose();
    }
    catch (Exception ex)
    {
        Log.Warning(ex, "Error disposing NpgsqlDataSource");
    }
    
    Log.Information("FileConverter application shutdown complete");
    Log.CloseAndFlush();
}

// Класс для конфигурации
public class AppSettings
{
    public string BaseUrl { get; set; } = string.Empty;
}

// Класс для доступа к DI из статических методов
public static class ServiceActivator
{
    private static IServiceProvider? _serviceProvider;

    public static void Configure(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
    }

    public static IServiceScope GetScope()
    {
        if (_serviceProvider == null)
        {
            throw new InvalidOperationException("ServiceActivator not initialized. Call Configure first.");
        }
        return _serviceProvider.CreateScope();
    }
}
