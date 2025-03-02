using Microsoft.Extensions.DependencyInjection;

namespace FileConverter.Services
{
    /// <summary>
    /// Позволяет получить доступ к сервисам из статического контекста
    /// </summary>
    public static class ServiceActivator
    {
        private static IServiceProvider? _serviceProvider;
        private static IServiceScope? _scope;

        public static void Configure(IServiceProvider serviceProvider)
        {
            _serviceProvider = serviceProvider;
        }

        public static IServiceScope? GetScope()
        {
            if (_serviceProvider == null)
                return null;
                
            return _scope ??= _serviceProvider.CreateScope();
        }
        
        public static void Reset()
        {
            if (_scope != null)
            {
                _scope.Dispose();
                _scope = null;
            }
        }
    }
} 