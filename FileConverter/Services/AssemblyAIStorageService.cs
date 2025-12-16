using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading.Tasks;
using FileConverter.Services.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace FileConverter.Services
{
    /// <summary>
    /// Сервис для загрузки файлов в AssemblyAI
    /// </summary>
    public class AssemblyAIStorageService : IAssemblyAIStorageService
    {
        private readonly ILogger<AssemblyAIStorageService> _logger;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly string _apiKey;
        private readonly string _apiBaseUrl;

        public AssemblyAIStorageService(
            ILogger<AssemblyAIStorageService> logger,
            IConfiguration configuration,
            IHttpClientFactory httpClientFactory)
        {
            _logger = logger;
            _httpClientFactory = httpClientFactory;
            
            // Получаем настройки из конфигурации
            _apiKey = configuration["AssemblyAI:ApiKey"] ?? throw new ArgumentNullException("AssemblyAI:ApiKey is not configured");
            
            // Поддерживаем EU сервер, если указан
            var useEuServer = configuration.GetValue<bool>("AssemblyAI:UseEuServer", false);
            _apiBaseUrl = useEuServer ? "https://api.eu.assemblyai.com" : "https://api.assemblyai.com";
            
            _logger.LogInformation("AssemblyAIStorageService инициализирован. Base URL: {BaseUrl}", _apiBaseUrl);
        }

        /// <summary>
        /// Загружает файл в AssemblyAI и возвращает upload_url
        /// </summary>
        public async Task<string> UploadFileAsync(string filePath, string contentType)
        {
            try
            {
                if (!File.Exists(filePath))
                {
                    throw new FileNotFoundException($"Файл не найден: {filePath}");
                }

                var httpClient = _httpClientFactory.CreateClient("default");
                httpClient.Timeout = TimeSpan.FromMinutes(3);

                using var fileStream = File.OpenRead(filePath);
                using var content = new StreamContent(fileStream);
                content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");

                var request = new HttpRequestMessage(HttpMethod.Post, $"{_apiBaseUrl}/v2/upload")
                {
                    Content = content
                };
                
                request.Headers.Add("Authorization", _apiKey);

                _logger.LogInformation("Загрузка файла в AssemblyAI: {FilePath} ({Size} bytes)", 
                    Path.GetFileName(filePath), fileStream.Length);

                using var response = await httpClient.SendAsync(request);
                
                if (!response.IsSuccessStatusCode)
                {
                    var errorContent = await response.Content.ReadAsStringAsync();
                    _logger.LogError("Ошибка загрузки файла в AssemblyAI. StatusCode: {StatusCode}, Response: {Response}", 
                        response.StatusCode, errorContent);
                    throw new HttpRequestException($"Ошибка загрузки в AssemblyAI: {response.StatusCode}. {errorContent}");
                }

                var responseContent = await response.Content.ReadAsStringAsync();
                var jsonDoc = JsonDocument.Parse(responseContent);
                
                if (!jsonDoc.RootElement.TryGetProperty("upload_url", out var uploadUrlElement))
                {
                    throw new InvalidOperationException("Ответ AssemblyAI не содержит upload_url");
                }

                var uploadUrl = uploadUrlElement.GetString();
                _logger.LogInformation("Файл успешно загружен в AssemblyAI. Upload URL получен для: {FilePath}", 
                    Path.GetFileName(filePath));

                return uploadUrl ?? throw new InvalidOperationException("upload_url пустой в ответе AssemblyAI");
            }
            catch (HttpRequestException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ошибка при загрузке файла в AssemblyAI: {FilePath}", filePath);
                throw;
            }
        }
    }
}

