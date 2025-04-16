using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Amazon.Runtime;
using Amazon;

namespace FileConverter.Services
{
    public class S3StorageService : IS3StorageService
    {
        private readonly IAmazonS3 _s3Client;
        private readonly ILogger<S3StorageService> _logger;
        private readonly string _bucketName;
        private readonly HttpClient _httpClient;
        private readonly string _serviceUrl;

        public S3StorageService(
            ILogger<S3StorageService> logger,
            IConfiguration configuration)
        {
            _logger = logger;
            
            // Получаем настройки из конфигурации
            _bucketName = configuration["AWS:S3:BucketName"] ?? throw new ArgumentNullException("AWS:S3:BucketName is not configured");
            _serviceUrl = configuration["AWS:S3:ServiceURL"] ?? throw new ArgumentNullException("AWS:S3:ServiceURL is not configured");
            var accessKey = configuration["AWS:S3:AccessKey"] ?? throw new ArgumentNullException("AWS:S3:AccessKey is not configured");
            var secretKey = configuration["AWS:S3:SecretKey"] ?? throw new ArgumentNullException("AWS:S3:SecretKey is not configured");
            
            // Создаем клиент S3 с настройками из конфигурации
            var credentials = new BasicAWSCredentials(accessKey, secretKey);
            var config = new AmazonS3Config
            {
                ServiceURL = _serviceUrl
            };
            
            _s3Client = new AmazonS3Client(credentials, config);
            _httpClient = new HttpClient();
        }

        public async Task<bool> FileExistsAsync(string url)
        {
            try
            {
                // Для внешних URL проверяем доступность через HEAD запрос
                using var response = await _httpClient.SendAsync(new HttpRequestMessage(HttpMethod.Head, url));
                return response.IsSuccessStatusCode;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error checking file existence: {url}");
                return false;
            }
        }

        public async Task<string> UploadFileAsync(string filePath, string contentType)
        {
            try
            {
                var key = $"{Guid.NewGuid()}{Path.GetExtension(filePath)}";
                var request = new PutObjectRequest
                {
                    BucketName = _bucketName,
                    Key = key,
                    FilePath = filePath,
                    ContentType = contentType,
                    CannedACL = S3CannedACL.PublicRead
                };

                await _s3Client.PutObjectAsync(request);
                return $"{_serviceUrl}/{_bucketName}/{key}";
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error uploading file: {filePath}");
                throw;
            }
        }


        public async Task<bool> DeleteFileAsync(string url)
        {
            try
            {
                if (!url.Contains(_serviceUrl))
                {
                    _logger.LogWarning($"Cannot delete external URL: {url}");
                    return false;
                }

                // Извлекаем ключ из нового формата URL
                var uri = new Uri(url);
                var path = uri.AbsolutePath.TrimStart('/');
                var segments = path.Split('/');
                
                // Формат URL: {_serviceUrl}/{_bucketName}/{key}
                if (segments.Length < 2)
                {
                    _logger.LogWarning($"Invalid S3 URL format: {url}");
                    return false;
                }
                
                // Ключ находится после имени бакета
                var key = string.Join("/", segments.Skip(1));
                
                var request = new DeleteObjectRequest
                {
                    BucketName = _bucketName,
                    Key = key
                };

                await _s3Client.DeleteObjectAsync(request);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error deleting file: {url}");
                return false;
            }
        }

        public async Task<byte[]> DownloadFileAsync(string url)
        {
            try
            {
                // Для внешних URL используем HttpClient
                using var response = await _httpClient.GetAsync(url);
                response.EnsureSuccessStatusCode();
                return await response.Content.ReadAsByteArrayAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error downloading file: {url}");
                throw;
            }
        }
    }
} 