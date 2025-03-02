using Serilog;
using Serilog.Events;
using Serilog.Filters;

namespace FileConverter
{
    internal static class LoggingConfiguration
    {
        public static void ConfigureLogging(this ConfigurationManager configuration, string applicationName)
        {
            Log.Logger = new LoggerConfiguration()
                .ReadFrom.Configuration(configuration)
                .Enrich.WithMachineName()
                .Enrich.WithProperty("Environment", Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT"))
                .Enrich.WithProperty("ApplicationName", applicationName)
                .Enrich.FromLogContext()
                .Enrich.WithThreadId()
                .Filter.ByExcluding(Matching.FromSource("Microsoft.AspNetCore.StaticFiles"))
                .Filter.ByExcluding(logEvent => 
                    logEvent.Properties.TryGetValue("RequestPath", out var requestPath) &&
                    requestPath.ToString().Contains("Microsoft.AspNetCore") &&
                    logEvent.Level < LogEventLevel.Error)
                .WriteTo.Console()
                .WriteTo.File(
                    $"Logs/{applicationName}-All-.log",
                    LogEventLevel.Information,
                    rollingInterval: RollingInterval.Day,
                    retainedFileCountLimit: 30)
                .WriteTo.File(
                    $"Logs/{applicationName}-Errors-.log",
                    LogEventLevel.Error,
                    rollingInterval: RollingInterval.Day,
                    retainedFileCountLimit: 30)
                .CreateLogger();
        }

        public static IHostBuilder UseSerilogLogging(this IHostBuilder builder)
        {
            return builder.UseSerilog();
        }
        
        public static WebApplicationBuilder UseSerilogLogging(this WebApplicationBuilder builder)
        {
            builder.Host.UseSerilog();
            return builder;
        }

        public static void LogApplicationStartup(this WebApplication app)
        {
            var hostEnvironment = app.Services.GetRequiredService<IHostEnvironment>();
            
            Log.Information("Starting {ApplicationName} in {EnvironmentName} mode...",
                hostEnvironment.ApplicationName,
                hostEnvironment.EnvironmentName);
        }

        public static void LogApplicationShutdown(this WebApplication app)
        {
            var hostEnvironment = app.Services.GetRequiredService<IHostEnvironment>();
            
            Log.Information("{ApplicationName} shutdown complete",
                hostEnvironment.ApplicationName);
            
            Log.CloseAndFlush();
        }
    }
} 