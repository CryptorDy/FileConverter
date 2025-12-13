using FileConverter.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace FileConverter.HealthChecks;

/// <summary>
/// Проверка доступности базы данных без создания новых ServiceProvider (важно для стабильности под нагрузкой).
/// </summary>
public sealed class DatabaseHealthCheck : IHealthCheck
{
    private readonly IServiceScopeFactory _scopeFactory;

    public DatabaseHealthCheck(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var canConnect = await dbContext.Database.CanConnectAsync(cancellationToken);
            return canConnect
                ? HealthCheckResult.Healthy("База данных доступна")
                : HealthCheckResult.Unhealthy("База данных недоступна");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("Проблема с подключением к базе данных", ex);
        }
    }
}


