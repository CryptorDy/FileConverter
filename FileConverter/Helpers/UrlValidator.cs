using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;

namespace FileConverter.Helpers
{
    public class UrlValidator
    {
        private readonly ILogger<UrlValidator> _logger;
        private readonly IConfiguration _configuration;
        private readonly IHttpClientFactory _httpClientFactory;
        
        // Maximum file size for download (default 500 MB)
        private readonly long _maxFileSize;
        
        // List of allowed content types from configuration
        private readonly HashSet<string> _allowedContentTypes;

        public UrlValidator(ILogger<UrlValidator> logger, IConfiguration configuration, IHttpClientFactory httpClientFactory)
        {
            _logger = logger;
            _configuration = configuration;
            _httpClientFactory = httpClientFactory;
            
            // Loading maximum file size from configuration (in bytes)
            _maxFileSize = _configuration.GetValue<long>("FileValidation:MaxFileSizeMB", 500) * 1024 * 1024;
            
            // Loading list of allowed content types from configuration
            _allowedContentTypes = new HashSet<string>(_configuration
                .GetSection("FileValidation:AllowedContentTypes")
                .Get<string[]>() ?? new string[] 
                {
                    "video/mp4", "video/quicktime", "video/x-msvideo", "video/x-ms-wmv",
                    "video/webm", "video/x-flv", "video/3gpp", "video/mpeg",
                    "audio/mpeg", "audio/mp3", "audio/ogg", "audio/wav", "audio/webm",
                    "application/octet-stream"
                });
                
            _logger.LogInformation($"Initialized URL validator. " +
                                  $"Max file size: {_maxFileSize / (1024.0 * 1024):F2} MB, " +
                                  $"Allowed content types: {string.Join(", ", _allowedContentTypes)}");
        }

        /// <summary>
        /// Checks if URL is safe and valid
        /// </summary>
        public bool IsUrlValid(string url)
        {
            try
            {
                // Check for null or empty string
                if (string.IsNullOrWhiteSpace(url))
                {
                    _logger.LogWarning("Empty URL received");
                    return false;
                }
                
                // Check that URL is valid
                if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) || 
                    uri.Scheme != "http" && uri.Scheme != "https")
                {
                    _logger.LogWarning($"Invalid URL: {url}");
                    return false;
                }
                
                // Check for local addresses (localhost, 127.0.0.1, etc.)
                if (IsLocalHost(uri))
                {
                    _logger.LogWarning($"Access to local addresses forbidden: {url}");
                    return false;
                }
                
                // Check for potentially dangerous files
                if (IsPotentiallyDangerousFile(url))
                {
                    _logger.LogWarning($"Potentially dangerous file type: {url}");
                    return false;
                }
                
                
                
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error validating URL: {url}", url);
                return false;
            }
        }
        
        /// <summary>
        /// Checks file size by URL (makes HEAD request)
        /// </summary>
        public async Task<bool> IsFileSizeValid(string url)
        {
            try
            {
                using var httpClient = _httpClientFactory.CreateClient("default");
                httpClient.DefaultRequestHeaders.Add("User-Agent", "FileConverter/1.0");
                
                // First make a HEAD request to check size
                using var request = new HttpRequestMessage(HttpMethod.Head, url);
                using var response = await httpClient.SendAsync(request);
                
                // Get Content-Length if available
                if (response.Content.Headers.ContentLength.HasValue)
                {
                    long fileSize = response.Content.Headers.ContentLength.Value;
                    if (fileSize > _maxFileSize)
                    {
                        _logger.LogWarning($"Maximum file size exceeded: {url}, size: {fileSize / (1024.0 * 1024):F2} MB");
                        return false;
                    }
                    
                    _logger.LogInformation($"File size for {url}: {fileSize / (1024.0 * 1024):F2} MB");
                    return true;
                }
                
                // If size is not specified in headers, we cannot check the size
                _logger.LogWarning($"Unable to determine file size for {url}");
                return true; // We assume the size is okay if we can't determine it
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error checking file size: {url}");
                return false; // In case of error, we consider the file invalid
            }
        }
        
        /// <summary>
        /// Checks file MIME type
        /// </summary>
        public async Task<(bool isValid, string contentType)> IsContentTypeValid(string url)
        {
            try
            {
                using var httpClient = _httpClientFactory.CreateClient("default");
                httpClient.DefaultRequestHeaders.Add("User-Agent", "FileConverter/1.0");
                
                // Make a HEAD request to check content type
                using var request = new HttpRequestMessage(HttpMethod.Head, url);
                using var response = await httpClient.SendAsync(request);
                
                string contentType = response.Content.Headers.ContentType?.MediaType ?? "";
                
                // Check against list of allowed content types
                if (string.IsNullOrEmpty(contentType))
                {
                    _logger.LogWarning($"Empty content type for {url}");
                    return (false, "empty");
                }
                
                // Special handling for text/plain - this may be incorrect type from some servers
                if (contentType == "text/plain")
                {
                    // Check file extension in URL
                    Uri uri = new Uri(url);
                    string filename = Path.GetFileName(uri.LocalPath);
                    string extension = Path.GetExtension(filename).ToLowerInvariant();
                    
                    // If the file has video or audio extension, allow it despite content type
                    if (!string.IsNullOrEmpty(extension) && 
                        (extension == ".mp4" || extension == ".mov" || extension == ".mp3" || 
                         extension == ".avi" || extension == ".webm" || extension == ".ogg"))
                    {
                        _logger.LogWarning($"Received text/plain for {url}, but file has extension {extension}. Allowing download.");
                        return (true, $"video/{extension.TrimStart('.')}");
                    }
                }
                
                if (!_allowedContentTypes.Contains(contentType))
                {
                    _logger.LogWarning($"Invalid content type for {url}: {contentType}");
                    return (false, contentType);
                }
                
                _logger.LogInformation($"Content type for {url}: {contentType}");
                return (true, contentType);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error checking content type: {url}");
                return (false, "error");
            }
        }
        
        // Helper methods
        private bool IsLocalHost(Uri uri)
        {
            string host = uri.Host.ToLower();
            return host == "localhost" || 
                   host == "127.0.0.1" || 
                   host == "::1" ||
                   host.EndsWith(".local") ||
                   host.EndsWith(".internal");
        }
        
        private bool IsIpAddress(string host)
        {
            // Check IPv4
            if (System.Net.IPAddress.TryParse(host, out _))
            {
                return true;
            }
            
            // Check for private IPv4 ranges in domain format
            string[] parts = host.Split('.');
            if (parts.Length == 4 && parts.All(p => int.TryParse(p, out _)))
            {
                return true;
            }
            
            return false;
        }
        
        private bool IsPotentiallyDangerousFile(string url)
        {
            // Check file extension if available
            string extension = Path.GetExtension(new Uri(url).AbsolutePath).ToLower();
            
            // List of potentially dangerous extensions
            string[] dangerousExtensions = { 
                ".exe", ".dll", ".bat", ".cmd", ".sh", ".ps1", ".vbs", ".wsf", ".reg", 
                ".hta", ".pif", ".scr", ".inf", ".msi", ".com", ".js", ".jse" 
            };
            
            return dangerousExtensions.Contains(extension);
        }
        
        /// <summary>
        /// Determines if URL belongs to a social network or video hosting service
        /// </summary>
        private bool IsSocialMediaUrl(Uri uri)
        {
            string host = uri.Host.ToLowerInvariant();
            
            // List of social media and video hosting domains
            string[] socialMediaDomains = new[]
            {
                "instagram.com", "fbcdn.net", "facebook.com", 
                "youtube.com", "youtu.be", "vimeo.com",
                "tiktok.com", "twitter.com", "twimg.com",
                "pinterest.com", "snapchat.com", "linkedin.com"
            };
            
            return socialMediaDomains.Any(domain => host.Contains(domain));
        }
    }
} 