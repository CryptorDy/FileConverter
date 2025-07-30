using System;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;

namespace FileConverter.Services
{
    /// <summary>
    /// HTTP-клиент для работы с отдельным сервисом Essentia
    /// </summary>
    public class HttpEssentiaClient : IDisposable
    {
        public class AudioAnalysis
        {
            public float tempo_bpm { get; set; }
            public float confidence { get; set; }
            public float[] beat_timestamps_sec { get; set; } = Array.Empty<float>();
            public float[] bpm_intervals { get; set; } = Array.Empty<float>();
            public int beats_detected { get; set; }
            public double rhythm_regularity { get; set; }
        }

        public class EssentiaAnalysisResponse
        {
            public string? Error { get; set; }
            public AudioAnalysis? AudioAnalysis { get; set; }
        }

        private readonly HttpClient _httpClient;
        private readonly ILogger<HttpEssentiaClient> _logger;
        private readonly string _essentiaServiceUrl;
        private bool _disposed = false;

        public HttpEssentiaClient(ILogger<HttpEssentiaClient> logger, IConfiguration configuration)
        {
            _logger = logger;
            _essentiaServiceUrl = configuration["ESSENTIA_SERVICE_URL"] ?? "http://localhost:8080";
            
            _httpClient = new HttpClient();
            _httpClient.Timeout = TimeSpan.FromMinutes(5); // Увеличиваем таймаут для анализа больших файлов
            
            _logger.LogInformation("HttpEssentiaClient инициализирован. URL сервиса: {ServiceUrl}", _essentiaServiceUrl);
        }

        /// <summary>
        /// Проверяет доступность сервиса Essentia
        /// </summary>
        public async Task<bool> IsServiceAvailableAsync()
        {
            try
            {
                var response = await _httpClient.GetAsync($"{_essentiaServiceUrl}/health");
                if (response.IsSuccessStatusCode)
                {
                    var content = await response.Content.ReadAsStringAsync();
                    var healthData = JsonConvert.DeserializeObject<dynamic>(content);
                    return healthData?.status == "healthy";
                }
                return false;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Не удалось проверить доступность сервиса Essentia");
                return false;
            }
        }

        /// <summary>
        /// Анализирует аудио файл через HTTP сервис
        /// </summary>
        public async Task<string> AnalyzeFromFileAsync(string audioFilePath)
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(HttpEssentiaClient));
            }

            try
            {
                _logger.LogDebug("Начинаем анализ аудио файла через HTTP: {AudioFilePath}", audioFilePath);

                // Подготавливаем запрос
                var requestData = new { file_path = audioFilePath };
                var jsonContent = JsonConvert.SerializeObject(requestData);
                var content = new StringContent(jsonContent, Encoding.UTF8, "application/json");

                // Отправляем POST запрос
                var response = await _httpClient.PostAsync($"{_essentiaServiceUrl}/analyze", content);
                
                var responseContent = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode)
                {
                    var errorMsg = $"HTTP ошибка {response.StatusCode}: {responseContent}";
                    _logger.LogError("Ошибка при анализе аудио: {Error}", errorMsg);
                    return JsonConvert.SerializeObject(new { error = errorMsg });
                }

                _logger.LogDebug("Анализ аудио завершен успешно");
                return responseContent;
            }
            catch (HttpRequestException ex)
            {
                var errorMsg = $"Ошибка сетевого соединения с сервисом Essentia: {ex.Message}";
                _logger.LogError(ex, "Сетевая ошибка при анализе аудио файла: {AudioFilePath}", audioFilePath);
                return JsonConvert.SerializeObject(new { error = errorMsg });
            }
            catch (TaskCanceledException ex) when (ex.InnerException is TimeoutException)
            {
                var errorMsg = "Таймаут при анализе аудио файла";
                _logger.LogError(ex, "Таймаут при анализе аудио файла: {AudioFilePath}", audioFilePath);
                return JsonConvert.SerializeObject(new { error = errorMsg });
            }
            catch (Exception ex)
            {
                var errorMsg = $"Неожиданная ошибка при анализе аудио: {ex.Message}";
                _logger.LogError(ex, "Неожиданная ошибка при анализе аудио файла: {AudioFilePath}", audioFilePath);
                return JsonConvert.SerializeObject(new { error = errorMsg });
            }
        }

        /// <summary>
        /// Анализирует аудио файл через GET запрос (альтернативный метод)
        /// </summary>
        public async Task<string> AnalyzeFromFileGetAsync(string audioFilePath)
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(HttpEssentiaClient));
            }

            try
            {
                _logger.LogDebug("Начинаем анализ аудио файла через HTTP GET: {AudioFilePath}", audioFilePath);

                // Кодируем путь для URL
                var encodedPath = Uri.EscapeDataString(audioFilePath);
                var response = await _httpClient.GetAsync($"{_essentiaServiceUrl}/analyze?file={encodedPath}");
                
                var responseContent = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode)
                {
                    var errorMsg = $"HTTP ошибка {response.StatusCode}: {responseContent}";
                    _logger.LogError("Ошибка при анализе аудио: {Error}", errorMsg);
                    return JsonConvert.SerializeObject(new { error = errorMsg });
                }

                _logger.LogDebug("Анализ аудио завершен успешно");
                return responseContent;
            }
            catch (Exception ex)
            {
                var errorMsg = $"Ошибка при анализе аудио: {ex.Message}";
                _logger.LogError(ex, "Ошибка при анализе аудио файла: {AudioFilePath}", audioFilePath);
                return JsonConvert.SerializeObject(new { error = errorMsg });
            }
        }

        public void Dispose()
        {
            if (_disposed) return;

            try
            {
                _httpClient?.Dispose();
                _logger.LogInformation("HttpEssentiaClient ресурсы освобождены");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Ошибка при освобождении ресурсов HttpEssentiaClient");
            }
            finally
            {
                _disposed = true;
            }
        }
    }
} 