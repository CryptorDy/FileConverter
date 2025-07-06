using FileConverter.Data;
using FileConverter.Models;
using System.Security.Cryptography;
using System.Text;
using FileConverter.Services.Interfaces;
using Microsoft.Extensions.Logging;
using System.Threading.Tasks;
using System;
using System.Threading.Channels;

namespace FileConverter.Services;

/// <summary>
/// Сервис для инициации процесса конвертации видео в аудио.
/// Отвечает за проверку задачи, поиск в кэше и добавление в очередь обработки.
/// </summary>
public class VideoConverter : IVideoConverter
{
    private readonly IMediaItemRepository _mediaItemRepository;
    private readonly ILogger<VideoConverter> _logger;
    private readonly IJobRepository _repository;
    private readonly UrlValidator _urlValidator;
    private readonly IConversionLogger _conversionLogger;
    private readonly ProcessingChannels _channels;
    private readonly IYoutubeDownloadService _youtubeDownloadService;

    /// <summary>
    /// Создает новый экземпляр сервиса VideoConverter.
    /// </summary>
    public VideoConverter(
        IMediaItemRepository mediaItemRepository,
        ILogger<VideoConverter> logger,
        IJobRepository repository,
        UrlValidator urlValidator,
        IConversionLogger conversionLogger,
        ProcessingChannels channels,
        IYoutubeDownloadService youtubeDownloadService)
    {
        _mediaItemRepository = mediaItemRepository;
        _logger = logger;
        _repository = repository;
        _urlValidator = urlValidator;
        _conversionLogger = conversionLogger;
        _channels = channels;
        _youtubeDownloadService = youtubeDownloadService;

        _logger.LogInformation("VideoConverter сервис инициализирован.");
    }

    /// <summary>
    /// Запускает процесс обработки видео: проверяет данные, кэш и добавляет в очередь скачивания.
    /// Этот метод теперь вызывается напрямую Hangfire или другими сервисами.
    /// </summary>
    /// <param name="jobId">Идентификатор задачи конвертации</param>
    public async Task ProcessVideo(string jobId)
    {
        _logger.LogInformation("ProcessVideo вызван для задачи {JobId}", jobId);
        try
        {
            // Находим задачу в базе данных
            var job = await _repository.GetJobByIdAsync(jobId);
            if (job == null)
            {
                await _conversionLogger.LogErrorAsync(jobId, $"Задание с ID {jobId} не найдено");
                _logger.LogWarning("Задача {JobId} не найдена в БД при вызове ProcessVideo.", jobId);
                return;
            }

            // Проверяем, не завершена ли уже задача (например, найдена в кэше ранее или обработана другим процессом)
            if (job.Status == ConversionStatus.Completed || job.Status == ConversionStatus.Failed)
            {
                 _logger.LogInformation("Задача {JobId} уже находится в конечном статусе {Status}. Обработка не требуется.", jobId, job.Status);
                 return;
            }
            
            // Проверяем, не находится ли задача уже в активной обработке (чтобы избежать дублирования в каналах)
            // Это может произойти, если RecoverStaleJobsAsync сработает одновременно с новым запросом
            // Добавляем проверку статуса перед добавлением в канал
            if (job.Status != ConversionStatus.Pending)
            {
                 _logger.LogInformation("Задача {JobId} находится в статусе {Status}, не добавляем повторно в очередь скачивания.", jobId, job.Status);
                 return;
            }

            await _conversionLogger.LogJobQueuedAsync(jobId, job.VideoUrl, "Задача проверена и готова к постановке в очередь скачивания");
            
            // Получаем хеш для поиска в репозитории
            string videoHash;
            try 
            {
                using (var sha = SHA256.Create())
                {
                    var hashBytes = sha.ComputeHash(Encoding.UTF8.GetBytes(job.VideoUrl));
                    videoHash = Convert.ToBase64String(hashBytes);
                }
            }
            catch (Exception hashEx)
            {
                 _logger.LogError(hashEx, "Задача {JobId}: Ошибка вычисления SHA256 хеша для URL {VideoUrl}", jobId, job.VideoUrl);
                 await _conversionLogger.LogErrorAsync(jobId, $"Ошибка вычисления хеша: {hashEx.Message}", hashEx.StackTrace);
                 await DbJobManager.UpdateJobStatusAsync(_repository, jobId, ConversionStatus.Failed, errorMessage: "Ошибка вычисления хеша URL");
                 return;
            }

            // Проверяем наличие в репозитории MediaItems (замена кэша)
            try
            {
                var existingItem = await _mediaItemRepository.FindByVideoHashAsync(videoHash);
                if (existingItem != null && !string.IsNullOrEmpty(existingItem.AudioUrl))
                {
                    _logger.LogInformation("Задача {JobId}: Найден готовый результат в MediaItems по хешу {VideoHash}. URL: {AudioUrl}", jobId, videoHash, existingItem.AudioUrl);
                    await _conversionLogger.LogCacheHitAsync(jobId, existingItem.AudioUrl, videoHash);
                    // Обновляем статус и сохраняем URL результата
                    await DbJobManager.UpdateJobStatusAsync(_repository, jobId, ConversionStatus.Completed, 
                        mp3Url: existingItem.AudioUrl, 
                        newVideoUrl: existingItem.VideoUrl);
                    return;
                }
            }
            catch (Exception repoCheckEx)
            {
                // Не фатально, продолжаем без кэша, но логируем
                 _logger.LogError(repoCheckEx, "Задача {JobId}: Ошибка при проверке MediaItems по хешу {VideoHash}", jobId, videoHash);
                 await _conversionLogger.LogWarningAsync(jobId, $"Ошибка при проверке репозитория MediaItems: {repoCheckEx.Message}");
            }

            // Проверяем безопасность URL
            if (!_urlValidator.IsUrlValid(job.VideoUrl))
            {
                _logger.LogWarning("Задача {JobId}: Обнаружен небезопасный URL: {VideoUrl}", jobId, job.VideoUrl);
                await _conversionLogger.LogWarningAsync(jobId, "Обнаружен небезопасный URL", job.VideoUrl);
                await DbJobManager.UpdateJobStatusAsync(_repository, jobId, ConversionStatus.Failed, 
                    errorMessage: "Обнаружен недопустимый или небезопасный URL");
                return;
            }

            // Логируем информацию о задаче перед добавлением в очередь
            _logger.LogInformation("Задача {JobId} добавляется в очередь скачивания (_downloadChannel). Текущий размер очереди: {QueueCount}", 
                jobId, _channels.DownloadChannel.Reader.Count);
                
            // Помещаем видео в очередь загрузки
            try 
            { 
                // Проверяем, является ли это YouTube видео
                if (_youtubeDownloadService.IsYoutubeUrl(job.VideoUrl))
                {
                    await _channels.YoutubeDownloadChannel.Writer.WriteAsync((jobId, job.VideoUrl));
                    await _conversionLogger.LogSystemInfoAsync($"Задание {jobId} добавлено в очередь YouTube скачивания");
                    _logger.LogInformation("Задача {JobId} успешно добавлена в YoutubeDownloadChannel.", jobId);
                }
                else
                {
                    await _channels.DownloadChannel.Writer.WriteAsync((jobId, job.VideoUrl));
                    await _conversionLogger.LogSystemInfoAsync($"Задание {jobId} добавлено в очередь на скачивание");
                    _logger.LogInformation("Задача {JobId} успешно добавлена в _downloadChannel.", jobId);
                }
                    
                // Обновляем LastAttemptAt, чтобы задача не считалась зависшей сразу после добавления
                job.LastAttemptAt = DateTime.UtcNow; 
                await _repository.UpdateJobAsync(job);
            } 
            catch(ChannelClosedException chEx) 
            { 
                 _logger.LogError(chEx, "Задача {JobId}: Не удалось записать в _downloadChannel, так как канал закрыт.", jobId);
                 await _conversionLogger.LogErrorAsync(jobId, "Не удалось записать задачу в очередь скачивания (канал закрыт).", chEx.StackTrace);
            }
            catch (Exception writeEx)
            {
                 _logger.LogError(writeEx, "Задача {JobId}: Ошибка при записи в _downloadChannel.", jobId);
                 await _conversionLogger.LogErrorAsync(jobId, $"Ошибка при записи задачи в очередь скачивания: {writeEx.Message}", writeEx.StackTrace);
            }
        }
        catch (Exception ex)
        {
             _logger.LogError(ex, "Задача {JobId}: Непредвиденная ошибка в ProcessVideo.", jobId);
            await _conversionLogger.LogErrorAsync(jobId, $"Критическая ошибка при инициации обработки: {ex.Message}", ex.StackTrace);
            
            // Пытаемся обновить статус задачи на Failed, если возможно
            try 
            { 
                 await DbJobManager.UpdateJobStatusAsync(_repository, jobId, ConversionStatus.Failed, 
                    errorMessage: $"Критическая ошибка инициации: {ex.Message}");
            } 
            catch (Exception updateEx)
            { 
                 _logger.LogError(updateEx, "Задача {JobId}: Не удалось обновить статус на Failed после критической ошибки в ProcessVideo.", jobId);
            }
        }
    }
}
