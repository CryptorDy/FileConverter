using System.Net;
using System.Text.Json;

namespace FileConverter.Middleware
{
    /// <summary>
    /// Промежуточное ПО для глобальной обработки исключений
    /// </summary>
    public class GlobalExceptionHandler
    {
        private readonly RequestDelegate _next;
        private readonly ILogger<GlobalExceptionHandler> _logger;
        private readonly IWebHostEnvironment _env;

        public GlobalExceptionHandler(
            RequestDelegate next,
            ILogger<GlobalExceptionHandler> logger,
            IWebHostEnvironment env)
        {
            _next = next;
            _logger = logger;
            _env = env;
        }

        public async Task InvokeAsync(HttpContext context)
        {
            try
            {
                await _next(context);
            }
            catch (Exception ex)
            {
                await HandleExceptionAsync(context, ex);
            }
        }

        private async Task HandleExceptionAsync(HttpContext context, Exception exception)
        {
            // Логируем исключение (без блокировок потоков)
            await LogExceptionAsync(context, exception);
            
            // Создаем структуру ответа
            var response = context.Response;
            response.ContentType = "application/json";
            
            // Устанавливаем код ответа в зависимости от типа исключения
            ErrorResponse errorResponse = new(
                StatusCode: DetermineStatusCode(exception),
                Message: GetExceptionMessage(exception),
                Details: _env.IsDevelopment() ? exception.StackTrace : null
            );
            
            // Устанавливаем код статуса HTTP
            response.StatusCode = (int)errorResponse.StatusCode;
            
            // Сериализуем ответ в JSON
            var result = JsonSerializer.Serialize(errorResponse);
            await response.WriteAsync(result);
        }

        private HttpStatusCode DetermineStatusCode(Exception exception)
        {
            // Определяем код статуса по типу исключения
            return exception switch
            {
                KeyNotFoundException => HttpStatusCode.NotFound,
                ArgumentException or ArgumentNullException => HttpStatusCode.BadRequest,
                UnauthorizedAccessException => HttpStatusCode.Unauthorized,
                InvalidOperationException => HttpStatusCode.BadRequest,
                IOException => HttpStatusCode.InternalServerError,
                TimeoutException => HttpStatusCode.RequestTimeout,
                _ => HttpStatusCode.InternalServerError
            };
        }

        private string GetExceptionMessage(Exception exception)
        {
            // В рабочей среде не возвращаем технические детали ошибок
            if (!_env.IsDevelopment())
            {
                return exception switch
                {
                    KeyNotFoundException => "Запрашиваемый ресурс не найден",
                    ArgumentException or ArgumentNullException => "Неверные параметры запроса",
                    UnauthorizedAccessException => "Недостаточно прав для выполнения операции",
                    InvalidOperationException => "Недопустимая операция",
                    _ => "Произошла внутренняя ошибка сервера"
                };
            }
            
            return exception.Message;
        }

        private async Task LogExceptionAsync(HttpContext context, Exception exception)
        {
            var request = context.Request;
            string requestBody = await GetRequestBodyAsync(context);
            
            _logger.LogError(
                exception,
                "Unhandled exception: {ExceptionType} when processing {Method} {Url} - Body: {RequestBody}",
                exception.GetType().Name,
                request.Method,
                request.Path.Value,
                requestBody);
            
            // Дополнительное логирование для критических ошибок
            if (exception is OutOfMemoryException || exception is StackOverflowException)
            {
                _logger.LogCritical(
                    "CRITICAL ERROR: {ExceptionType}. Immediate attention required!",
                    exception.GetType().Name);
            }
        }

        private async Task<string> GetRequestBodyAsync(HttpContext context)
        {
            try
            {
                // Пытаемся получить тело запроса, если оно доступно
                var request = context.Request;
                
                // Не пытаемся читать потенциально огромные тела (например, upload), чтобы не создавать лишнюю нагрузку в обработчике ошибок
                if (request.ContentLength.HasValue && request.ContentLength.Value > 64 * 1024)
                {
                    return $"[Тело запроса пропущено: {request.ContentLength.Value} bytes]";
                }

                request.EnableBuffering();
                
                if (request.Body.CanSeek)
                {
                    request.Body.Position = 0;
                    using var reader = new StreamReader(request.Body, leaveOpen: true);
                    var body = await reader.ReadToEndAsync();
                    request.Body.Position = 0;
                    
                    // Ограничиваем длину тела для логирования
                    return body.Length > 500 ? body.Substring(0, 500) + "..." : body;
                }
            }
            catch
            {
                // Игнорируем ошибки при получении тела запроса
            }
            
            return "[Тело запроса недоступно]";
        }
    }

    // Класс для структурированного ответа об ошибке
    public record ErrorResponse(
        HttpStatusCode StatusCode,
        string Message,
        string? Details = null,
        Guid? ErrorId = null)
    {
        public ErrorResponse(HttpStatusCode statusCode, string message, string? details)
            : this(statusCode, message, details, Guid.NewGuid())
        {
        }
    }

    // Расширение для регистрации middleware
    public static class GlobalExceptionHandlerExtensions
    {
        public static IApplicationBuilder UseGlobalExceptionHandler(this IApplicationBuilder app)
        {
            return app.UseMiddleware<GlobalExceptionHandler>();
        }
    }
} 