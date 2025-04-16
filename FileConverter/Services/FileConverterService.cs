using Xabe.FFmpeg;
using Microsoft.Extensions.Configuration;
using System.Diagnostics;

namespace FileConverter.Services
{
    public class FileConverterService : IFileConverterService
    {
        private readonly IS3StorageService _s3StorageService;
        private readonly ILogger<FileConverterService> _logger;
        private readonly string _tempPath;
        private readonly IConfiguration _configuration;

        public FileConverterService(
            IS3StorageService s3StorageService,
            ILogger<FileConverterService> logger,
            IConfiguration configuration)
        {
            _s3StorageService = s3StorageService;
            _logger = logger;
            _configuration = configuration;
            _tempPath = Path.Combine(Path.GetTempPath(), "AiDiscussion", "Conversions");
            Directory.CreateDirectory(_tempPath);
            
            // Получаем путь к ffmpeg из конфигурации, с запасным вариантом
            string ffmpegPath = _configuration["AppSettings:FFmpegPath"] ?? "/usr/bin";
            _logger.LogInformation("Using FFmpeg path for Linux: {Path}", ffmpegPath);
            
            // На Linux используются исполняемые файлы без расширения .exe
            FFmpeg.SetExecutablesPath(ffmpegPath);
            
            // Проверяем существование файлов FFmpeg
            string ffmpegExe = Path.Combine(ffmpegPath, "ffmpeg");
            string ffprobeExe = Path.Combine(ffmpegPath, "ffprobe");
            
            if (!File.Exists(ffmpegExe))
                _logger.LogWarning("FFmpeg executable not found at path: {Path}", ffmpegExe);
            
            if (!File.Exists(ffprobeExe))
                _logger.LogWarning("FFprobe executable not found at path: {Path}", ffprobeExe);
        }

        public async Task<List<string>> FromVideoToMP3Async(List<string> sourceUrls)
        {
            var mp3Urls = new List<string>();
            var tasks = new List<Task>();

            foreach (var sourceUrl in sourceUrls)
            {
                tasks.Add(ConvertVideoToMP3Async(sourceUrl, mp3Urls));
            }

            await Task.WhenAll(tasks);
            return mp3Urls;
        }

        private async Task ConvertVideoToMP3Async(string sourceUrl, List<string> mp3Urls)
        {
            string videoPath = string.Empty;
            string mp3Path = string.Empty;
            var sw = Stopwatch.StartNew();

            try
            {
                _logger.LogInformation($"Starting file conversion: {sourceUrl}");

                videoPath = await DownloadVideoAsync(sourceUrl);
                mp3Path = await ConvertToMP3Async(videoPath);

                var contentType = "audio/mpeg";
                var mp3Url = await _s3StorageService.UploadFileAsync(mp3Path, contentType);

                lock (mp3Urls)
                {
                    mp3Urls.Add(mp3Url);
                }
                
                sw.Stop();
                _logger.LogInformation($"Conversion completed successfully: {sourceUrl} -> {mp3Url}, Time: {sw.ElapsedMilliseconds} ms");
            }
            catch (Exception ex)
            {
                sw.Stop();
                _logger.LogError(ex, $"Error converting file: {sourceUrl}, Time: {sw.ElapsedMilliseconds} ms");
                // В зависимости от политики можно добавлять null или URL с ошибкой
                // lock (mp3Urls) { mp3Urls.Add(null); } 
            }
            finally
            {
                CleanupTempFiles(videoPath, mp3Path);
            }
        }

        private async Task<string> DownloadVideoAsync(string url)
        {
            var videoData = await _s3StorageService.DownloadFileAsync(url);
            var extension = Path.GetExtension(url);
            if (string.IsNullOrEmpty(extension))
            {
                // Пытаемся определить расширение по URL или стандартно .tmp
                extension = new Uri(url).Segments.LastOrDefault()?.Contains(".") == true 
                    ? Path.GetExtension(new Uri(url).Segments.Last()) 
                    : ".tmp";
            }
            
            var videoPath = Path.Combine(_tempPath, $"{Guid.NewGuid()}{extension}");
            await File.WriteAllBytesAsync(videoPath, videoData);
            return videoPath;
        }

        private async Task<string> ConvertToMP3Async(string videoPath)
        {
            var outputPath = Path.Combine(_tempPath, $"{Guid.NewGuid()}.mp3");
            var mediaInfo = await FFmpeg.GetMediaInfo(videoPath);

            await FFmpeg.Conversions.New()
                .AddStream(mediaInfo.AudioStreams)
                .SetOutputFormat("mp3")
                .SetAudioBitrate(128000)
                .SetOutput(outputPath)
                .Start();

            if (!File.Exists(outputPath))
                throw new InvalidOperationException($"Conversion failed: {videoPath}");

            return outputPath;
        }

        private void CleanupTempFiles(params string[] paths)
        {
            foreach (var path in paths)
            {
                if (path == null || !File.Exists(path)) continue;

                try
                {
                    File.Delete(path);
                    _logger.LogInformation($"Temporary file deleted: {path}");
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, $"Error deleting file: {path}");
                }
            }
        }
    }
} 