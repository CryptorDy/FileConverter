using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

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
                _logger.LogError(ex, "Ошибка при выполнении действия с контекстом базы данных");
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
                _logger.LogError(ex, "Ошибка при выполнении функции с контекстом базы данных");
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
                _logger.LogError(ex, "Ошибка при асинхронном выполнении действия с контекстом базы данных");
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
            catch (ObjectDisposedException ex)
            {
                _logger.LogError(ex, "Контекст базы данных был уничтожен во время выполнения операции");
                // Пытаемся повторить операцию с новым контекстом
                using var newDbContext = CreateDbContext();
                try
                {
                    _logger.LogInformation("Повторная попытка выполнения операции с новым контекстом");
                    return await func(newDbContext);
                }
                catch (Exception retryEx)
                {
                    _logger.LogError(retryEx, "Ошибка при повторной попытке выполнения операции с новым контекстом");
                    throw;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ошибка при асинхронном выполнении функции с контекстом базы данных");
                throw;
            }
        }
    }
} 