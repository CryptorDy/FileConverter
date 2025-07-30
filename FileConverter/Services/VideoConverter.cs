using FileConverter.Data;
using FileConverter.Models;
using System.Security.Cryptography;
using System.Text;
using FileConverter.Services.Interfaces;
using FileConverter.Helpers;
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
            
            // Проверка кэша и обновление статуса теперь выполняется в DownloadBackgroundService
            // после получения реального хеша файла для более точного кэширования
            
            await _conversionLogger.LogJobQueuedAsync(jobId, job.VideoUrl, "Задача проверена и готова к постановке в очередь скачивания");

            // Валидация URL теперь выполняется в DbJobManager.EnqueueBatchJobs

            // Логируем информацию о задаче перед добавлением в очередь
            _logger.LogInformation("Задача {JobId} добавляется в очередь скачивания (_downloadChannel). Текущий размер очереди: {QueueCount}", 
                jobId, _channels.DownloadChannel.Reader.Count);
                
            // Помещаем видео в очередь загрузки
            try 
            { 
                bool writeSuccessful = false;
                
                // Проверяем, является ли это YouTube видео
                if (_youtubeDownloadService.IsYoutubeUrl(job.VideoUrl))
                {
                    writeSuccessful = _channels.YoutubeDownloadChannel.Writer.TryWrite((jobId, job.VideoUrl));
                    if (writeSuccessful)
                    {
                        await _conversionLogger.LogSystemInfoAsync($"Задание {jobId} добавлено в очередь YouTube скачивания");
                        _logger.LogInformation("Задача {JobId} успешно добавлена в YoutubeDownloadChannel.", jobId);
                    }
                    else
                    {
                        _logger.LogWarning("Задача {JobId}: YouTube очередь переполнена, задача отброшена.", jobId);
                        await _conversionLogger.LogErrorAsync(jobId, "YouTube очередь переполнена, задача не может быть обработана сейчас");
                        await DbJobManager.UpdateJobStatusAsync(_repository, jobId, ConversionStatus.Failed, 
                            errorMessage: "Система перегружена, очередь YouTube переполнена");
                    }
                }
                else
                {
                    writeSuccessful = _channels.DownloadChannel.Writer.TryWrite((jobId, job.VideoUrl));
                    if (writeSuccessful)
                    {
                        await _conversionLogger.LogSystemInfoAsync($"Задание {jobId} добавлено в очередь на скачивание");
                        _logger.LogInformation("Задача {JobId} успешно добавлена в _downloadChannel.", jobId);
                    }
                    else
                    {
                        _logger.LogWarning("Задача {JobId}: Очередь скачивания переполнена, задача отброшена.", jobId);
                        await _conversionLogger.LogErrorAsync(jobId, "Очередь скачивания переполнена, задача не может быть обработана сейчас");
                        await DbJobManager.UpdateJobStatusAsync(_repository, jobId, ConversionStatus.Failed, 
                            errorMessage: "Система перегружена, очередь скачивания переполнена");
                    }
                }
                
                // LastAttemptAt уже обновлен атомарно в TryUpdateJobStatusIfAsync
            } 
            catch(ChannelClosedException chEx) 
            { 
                 _logger.LogError(chEx, "Задача {JobId}: Не удалось записать в очередь, так как канал закрыт.", jobId);
                 await _conversionLogger.LogErrorAsync(jobId, "Не удалось записать задачу в очередь (канал закрыт).", chEx.StackTrace);
                 await DbJobManager.UpdateJobStatusAsync(_repository, jobId, ConversionStatus.Failed, 
                    errorMessage: "Система недоступна (канал закрыт)");
            }
            catch (Exception writeEx)
            {
                 _logger.LogError(writeEx, "Задача {JobId}: Ошибка при записи в очередь.", jobId);
                 await _conversionLogger.LogErrorAsync(jobId, $"Ошибка при записи задачи в очередь: {writeEx.Message}", writeEx.StackTrace);
                 await DbJobManager.UpdateJobStatusAsync(_repository, jobId, ConversionStatus.Failed, 
                    errorMessage: $"Ошибка постановки в очередь: {writeEx.Message}");
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
