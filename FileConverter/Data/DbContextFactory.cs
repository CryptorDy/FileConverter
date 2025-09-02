using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using System.Text;

namespace FileConverter.Data
{
    /// <summary>
    /// Фабрика для создания и безопасного использования контекста базы данных
    /// </summary>
    public class DbContextFactory
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly ILogger<DbContextFactory> _logger;

        public DbContextFactory(IServiceProvider serviceProvider, ILogger<DbContextFactory> logger)
        {
            _serviceProvider = serviceProvider;
            _logger = logger;
        }

        /// <summary>
        /// Создает новый экземпляр контекста базы данных
        /// </summary>
        public AppDbContext CreateDbContext()
        {
            return _serviceProvider.GetRequiredService<AppDbContext>();
        }

        /// <summary>
        /// Выполняет действие с новым экземпляром контекста базы данных
        /// </summary>
        public void ExecuteWithDbContext(Action<AppDbContext> action)
        {
            using var dbContext = CreateDbContext();
            try
            {
                action(dbContext);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error executing action with DbContext");
                throw;
            }
        }

        /// <summary>
        /// Выполняет действие и возвращает результат с новым экземпляром контекста базы данных
        /// </summary>
        public T ExecuteWithDbContext<T>(Func<AppDbContext, T> func)
        {
            using var dbContext = CreateDbContext();
            try
            {
                return func(dbContext);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error executing function with DbContext");
                throw;
            }
        }

        /// <summary>
        /// Асинхронно выполняет действие с новым экземпляром контекста базы данных
        /// </summary>
        public async Task ExecuteWithDbContextAsync(Func<AppDbContext, Task> func)
        {
            using var dbContext = CreateDbContext();
            try
            {
                await func(dbContext);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error executing async action with DbContext");
                throw;
            }
        }

        /// <summary>
        /// Асинхронно выполняет действие и возвращает результат с новым экземпляром контекста базы данных
        /// </summary>
        public async Task<T> ExecuteWithDbContextAsync<T>(Func<AppDbContext, Task<T>> func)
        {
            using var dbContext = CreateDbContext();
            try
            {
                return await func(dbContext);
            }
            catch (DbUpdateException dbEx)
            {
                var details = BuildDbUpdateExceptionDetails(dbEx);
                _logger.LogError(dbEx, "DbUpdateException during async function with DbContext. Details: {Details}", details);
                throw;
            }
            catch (ObjectDisposedException ex)
            {
                _logger.LogError(ex, "Контекст базы данных был уничтожен во время выполнения операции");
                // Пытаемся повторить операцию с новым контекстом
                using var newDbContext = CreateDbContext();
                try
                {
                    _logger.LogInformation("Повторная попытка выполнения операции с новым контекстом");
                    try
                    {
                        return await func(newDbContext);
                    }
                    catch (DbUpdateException retryDbEx)
                    {
                        var details = BuildDbUpdateExceptionDetails(retryDbEx);
                        _logger.LogError(retryDbEx, "DbUpdateException during retry with new DbContext. Details: {Details}", details);
                        throw;
                    }
                }
                catch (Exception retryEx)
                {
                    _logger.LogError(retryEx, "Ошибка при повторной попытке выполнения операции с новым контекстом");
                    throw;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error executing async function with DbContext");
                throw;
            }
        }

        private static string BuildDbUpdateExceptionDetails(DbUpdateException exception)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"Exception: {exception.GetType().FullName}");
            sb.AppendLine($"Message: {exception.Message}");
            if (exception.InnerException != null)
            {
                sb.AppendLine($"Inner: {exception.InnerException.GetType().FullName}: {exception.InnerException.Message}");
            }
            try
            {
                foreach (var entry in exception.Entries)
                {
                    sb.AppendLine($"Entry: {entry.Entity.GetType().FullName}, State: {entry.State}");
                    foreach (var prop in entry.Properties)
                    {
                        sb.AppendLine($" - {prop.Metadata.Name} = {prop.CurrentValue}");
                    }
                }
            }
            catch
            {
            }
            return sb.ToString();
        }
    }
} 