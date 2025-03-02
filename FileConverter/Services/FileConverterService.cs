using Xabe.FFmpeg;

namespace FileConverter.Services
{
    public class FileConverterService : IFileConverterService
    {
        private readonly IS3StorageService _s3StorageService;
        private readonly ILogger<FileConverterService> _logger;
        private readonly string _tempPath;

        public FileConverterService(
            IS3StorageService s3StorageService,
            ILogger<FileConverterService> logger)
        {
            _s3StorageService = s3StorageService;
            _logger = logger;
            _tempPath = Path.Combine(Path.GetTempPath(), "AiDiscussion", "Conversions");
            Directory.CreateDirectory(_tempPath);
            
            // Используем папку ffmpeg, которая содержит исполняемые файлы FFmpeg
            FFmpeg.SetExecutablesPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ffmpeg"));
        }

        public async Task<List<string>> FromVideoToMP3Async(List<string> sourceUrls)
        {
            var resultUrls = new List<string>();
            using var httpClient = new HttpClient();

            foreach (var sourceUrl in sourceUrls)
            {
                string tempVideoPath = null;
                string tempAudioPath = null;

                try
                {
                    _logger.LogInformation($"Начало обработки: {sourceUrl}");

                    // 1. Скачивание видео
                    tempVideoPath = await DownloadSourceFileAsync(sourceUrl, httpClient);

                    // 2. Конвертация в MP3
                    tempAudioPath = await ConvertToMp3Async(tempVideoPath);
                    
                    // 3. Загрузка в хранилище
                    string s3Url = await _s3StorageService.UploadFileAsync(tempAudioPath, "audio/mpeg");

                    resultUrls.Add(s3Url);
                    _logger.LogInformation($"Успешная конвертация: {sourceUrl} -> {s3Url}");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, $"Ошибка конвертации {sourceUrl}");
                    throw;
                }
                finally
                {
                    CleanupTempFiles(tempVideoPath, tempAudioPath);
                }
            }

            return resultUrls;
        }

        private async Task<string> DownloadSourceFileAsync(string url, HttpClient httpClient)
        {
            // Получаем расширение файла из URL
            string extension = Path.GetExtension(url);
            
            // Очищаем расширение от параметров URL
            if (!string.IsNullOrEmpty(extension))
            {
                // Обрезаем всё после первого вхождения '?' или '#'
                int queryIndex = extension.IndexOfAny(new[] { '?', '#' });
                if (queryIndex > 0)
                {
                    extension = extension.Substring(0, queryIndex);
                }
            }

            // Если расширение отсутствует или некорректное, используем .mp4 по умолчанию
            if (string.IsNullOrEmpty(extension) || extension.Length <= 1)
            {
                extension = ".mp4"; // Стандартное расширение по умолчанию
            }

            var tempPath = Path.Combine(_tempPath, $"{Guid.NewGuid()}{extension}");
            byte[] fileData;

            if (await _s3StorageService.FileExistsAsync(url))
            {
                fileData = await _s3StorageService.DownloadFileAsync(url);
                _logger.LogInformation($"Видео скачано из S3: {url}");
            }
            else
            {
                fileData = await httpClient.GetByteArrayAsync(url);
                _logger.LogInformation($"Видео загружено по ссылке: {url}");
            }

            await File.WriteAllBytesAsync(tempPath, fileData);
            return tempPath;
        }

        private async Task<string> ConvertToMp3Async(string videoPath)
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
                throw new InvalidOperationException($"Конвертация не удалась: {videoPath}");

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
                    _logger.LogInformation($"Временный файл удалён: {path}");
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, $"Ошибка удаления файла: {path}");
                }
            }
        }
    }
} 