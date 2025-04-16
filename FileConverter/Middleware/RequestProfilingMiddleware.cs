using System.Diagnostics;
using FileConverter.Services;

namespace FileConverter.Middleware
{
    /// <summary>
    /// Промежуточное ПО для профилирования HTTP запросов
    /// </summary>
    public class RequestProfilingMiddleware
    {
        private readonly RequestDelegate _next;
        private readonly ILogger<RequestProfilingMiddleware> _logger;
        private readonly MetricsCollector _metrics;

        public RequestProfilingMiddleware(
            RequestDelegate next, 
            ILogger<RequestProfilingMiddleware> logger,
            MetricsCollector metrics)
        {
            _next = next;
            _logger = logger;
            _metrics = metrics;
        }

        public async Task InvokeAsync(HttpContext context)
        {
            // Получаем маршрут запроса для метрики
            string route = GetRouteTemplate(context);
            string metricName = $"http_{context.Request.Method.ToLower()}_{route}";
            
            // Запускаем таймер
            var stopwatch = Stopwatch.StartNew();
            bool isSuccess = true;
            
            try
            {
                await _next(context);
                
                // Считаем неуспешными запросы с кодом 4xx и 5xx
                isSuccess = context.Response.StatusCode < 400;
            }
            catch
            {
                isSuccess = false;
                throw;
            }
            finally
            {
                stopwatch.Stop();
                long durationMs = stopwatch.ElapsedMilliseconds;
                
                // Записываем метрику и при необходимости логируем
                _metrics.RecordMetric(metricName, durationMs, isSuccess, context.TraceIdentifier);
                
                if (durationMs > 1000)  // Запросы дольше 1 секунды
                {
                    _logger.LogWarning(
                        "Slow query: {Method} {Path} - {Duration} ms, Code: {StatusCode}",
                        context.Request.Method,
                        context.Request.Path,
                        durationMs,
                        context.Response.StatusCode);
                }
                else if (context.Response.StatusCode >= 400)  // Ошибочные ответы
                {
                    _logger.LogWarning(
                        "Request with error: {Method} {Path} - Code: {StatusCode}",
                        context.Request.Method,
                        context.Request.Path,
                        context.Response.StatusCode);
                }
                
                // Добавляем заголовки для отладки, если включен режим разработки
                if (Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") == "Development")
                {
                    context.Response.Headers.Add("X-Response-Time-Ms", durationMs.ToString());
                }
            }
        }

        /// <summary>
        /// Получает шаблон маршрута для более информативных метрик
        /// </summary>
        private string GetRouteTemplate(HttpContext context)
        {
            var endpoint = context.GetEndpoint();
            if (endpoint == null)
            {
                return "unknown";
            }
            
            var routePattern = (endpoint as Microsoft.AspNetCore.Routing.RouteEndpoint)?.RoutePattern?.RawText;
            if (string.IsNullOrEmpty(routePattern))
            {
                // Если шаблон недоступен, используем путь запроса
                var path = context.Request.Path.Value ?? "/";
                // Заменяем параметры типа /users/123 на /users/{id}
                path = System.Text.RegularExpressions.Regex.Replace(
                    path, 
                    @"/[0-9a-f]{8}(-[0-9a-f]{4}){3}-[0-9a-f]{12}|/\d+", 
                    "/{id}");
                
                // Заменяем слэши на подчеркивания для имени метрики
                return path.TrimStart('/').Replace('/', '_');
            }
            
            // Заменяем слэши на подчеркивания для имени метрики
            return routePattern.TrimStart('/').Replace('/', '_').Replace('{', '_').Replace('}', '_');
        }
    }

    // Расширение для регистрации middleware
    public static class RequestProfilingMiddlewareExtensions
    {
        public static IApplicationBuilder UseRequestProfiling(this IApplicationBuilder app)
        {
            return app.UseMiddleware<RequestProfilingMiddleware>();
        }
    }
} 