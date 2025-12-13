using FileConverter.Services.Interfaces;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace FileConverter.HealthChecks;

/// <summary>
/// Проверка доступности директории временных файлов.
/// </summary>
public sealed class TempStorageHealthCheck : IHealthCheck
{
    private readonly ITempFileManager _tempFileManager;

    public TempStorageHealthCheck(ITempFileManager tempFileManager)
    {
        _tempFileManager = tempFileManager;
    }

    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var tempDir = _tempFileManager.GetTempDirectory();

            if (!Directory.Exists(tempDir))
            {
                return Task.FromResult(HealthCheckResult.Degraded(
                    $"Директория временных файлов не существует: {tempDir}"));
            }

            // Проверяем, можно ли создать файл
            var testFile = Path.Combine(tempDir, "healthcheck.tmp");
            File.WriteAllText(testFile, "test");
            File.Delete(testFile);

            return Task.FromResult(HealthCheckResult.Healthy("Хранилище временных файлов доступно"));
        }
        catch (Exception ex)
        {
            return Task.FromResult(HealthCheckResult.Unhealthy("Проблема с хранилищем временных файлов", ex));
        }
    }
}


