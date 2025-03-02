using FileConverter.Data;
using FileConverter.Services;
using FileConverter.Middleware;
using Hangfire;
using Hangfire.MemoryStorage;
using Hangfire.PostgreSql;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using System.Threading.RateLimiting;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Http;  // Для HttpClient
using Serilog;

var builder = WebApplication.CreateBuilder(args);

// Настройка Serilog
Log.Logger = new LoggerConfiguration()
    .ReadFrom.Configuration(builder.Configuration)
    .Enrich.WithMachineName()
    .Enrich.WithProperty("Environment", Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT"))
    .Enrich.WithProperty("ApplicationName", "FileConverter")
    .Enrich.FromLogContext()
    .Enrich.WithThreadId()
    .WriteTo.Console()
    .WriteTo.File(
        $"Logs/FileConverter-All-.log",
        Serilog.Events.LogEventLevel.Information,
        rollingInterval: Serilog.RollingInterval.Day,
        retainedFileCountLimit: 30)
    .WriteTo.File(
        $"Logs/FileConverter-Errors-.log",
        Serilog.Events.LogEventLevel.Error,
        rollingInterval: Serilog.RollingInterval.Day,
        retainedFileCountLimit: 30)
    .CreateLogger();

builder.Host.UseSerilog();

// Настройка HTTP клиента
builder.Services.AddHttpClient("video-downloader", client =>
{
    client.Timeout = TimeSpan.FromMinutes(
        builder.Configuration.GetValue<int>("Performance:DownloadTimeoutMinutes", 30));
});

// Конфигурация для высоких нагрузок
builder.WebHost.ConfigureKestrel(options =>
{
    // Увеличиваем максимальный размер тела запроса до 100 МБ
    options.Limits.MaxRequestBodySize = 104857600; // 100 MB
    
    // Увеличиваем время ожидания для долгих операций
    options.Limits.KeepAliveTimeout = TimeSpan.FromMinutes(10);
    options.Limits.RequestHeadersTimeout = TimeSpan.FromMinutes(5);
});

// Настройка контекста базы данных
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql(
        builder.Configuration.GetConnectionString("DefaultConnection"),
        npgsqlOptions => 
        {
            npgsqlOptions.EnableRetryOnFailure(3);
            npgsqlOptions.CommandTimeout(60); // Увеличиваем таймаут команды до 60 сек
        }
    )
);

// Add services to the container.
builder.Services.AddControllers();
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

// Регистрируем Hangfire для фоновых задач
builder.Services.AddHangfire(config =>
{
    // Используем PostgreSQL для продакшна
    if (builder.Environment.IsProduction())
    {
        var connectionString = builder.Configuration.GetConnectionString("HangfireConnection");
        // Используем новую рекомендуемую перегрузку с Action<PostgreSqlBootstrapperOptions>
        config.UsePostgreSqlStorage((Action<PostgreSqlBootstrapperOptions>)(options =>
        {
            options.UseNpgsqlConnection(connectionString);
        }), new PostgreSqlStorageOptions
        {
            PrepareSchemaIfNecessary = true,
            QueuePollInterval = TimeSpan.FromSeconds(15),
            InvisibilityTimeout = TimeSpan.FromMinutes(5),
            DistributedLockTimeout = TimeSpan.FromMinutes(5)
        });
    }
    else
    {
        // В разработке используем in-memory storage
        config.UseMemoryStorage();
    }
    
    // Настройка обработки очередей
    config.UseRecommendedSerializerSettings();
});

// Настраиваем сервер обработки Hangfire
builder.Services.AddHangfireServer(options =>
{
    options.WorkerCount = Math.Max(Environment.ProcessorCount, 4); // Минимум 4 воркера
    options.Queues = new[] { "critical", "default", "low" }; // Приоритеты очередей
    options.ServerName = "VideoConverter";
});

// Регистрируем репозиторий и сервисы
builder.Services.AddScoped<IJobRepository, JobRepository>();
builder.Services.AddSingleton<IS3StorageService, LocalStorageService>();
builder.Services.AddScoped<IFileConverterService, FileConverterService>();
builder.Services.AddScoped<IJobManager, DbJobManager>(); // Заменяем на реализацию с БД
builder.Services.AddScoped<IVideoProcessor, VideoProcessor>();

// Кэширование и временные файлы
builder.Services.AddSingleton<CacheManager>();
builder.Services.AddSingleton<DistributedCacheManager>();
builder.Services.AddSingleton<ITempFileManager, TempFileManager>();
builder.Services.AddScoped<TempFileCleanupJob>();
builder.Services.AddScoped<Mp3CleanupJob>(); // Добавляем сервис для очистки MP3
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
            """{"error":"Слишком много запросов. Пожалуйста, повторите попытку позже."}""", 
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

var app = builder.Build();

// Конфигурируем ServiceActivator для доступа к DI из статических методов
ServiceActivator.Configure(app.Services);

// Миграция базы данных при запуске
if (app.Environment.IsProduction())
{
    // В продакшене проверяем и применяем миграции автоматически
    using (var scope = app.Services.CreateScope())
    {
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        dbContext.Database.Migrate();
    }
}

// Глобальная обработка исключений
app.UseGlobalExceptionHandler();

// Профилирование HTTP запросов
app.UseRequestProfiling();

// Ограничение скорости запросов
app.UseRateLimiter();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
    
    // Добавляем панель управления Hangfire только в режиме разработки
    app.UseHangfireDashboard("/hangfire", new DashboardOptions
    {
        DashboardTitle = "Панель управления конвертацией видео",
        DisplayStorageConnectionString = false,
        IsReadOnlyFunc = (context) => false
    });
}

app.UseHttpsRedirection();
app.UseStaticFiles(); // Для доступа к файлам в wwwroot

app.UseCors("SecureCorsPolicy"); // Используем безопасную CORS политику

app.UseAuthorization();

app.MapControllers();
app.MapHangfireDashboard();

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

// Запускаем фоновые задачи
TempFileCleanupJob.ScheduleJobs();
Mp3CleanupJob.ScheduleJobs(); // Запускаем очистку MP3

// Логируем запуск приложения
Log.Information("Приложение FileConverter запущено в среде {Environment}", 
    app.Environment.EnvironmentName);

try
{
    app.Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Приложение FileConverter аварийно завершило работу");
}
finally
{
    Log.Information("Приложение FileConverter завершило работу");
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
            throw new InvalidOperationException("ServiceActivator не инициализирован. Сначала вызовите Configure.");
        }
        return _serviceProvider.CreateScope();
    }
}
