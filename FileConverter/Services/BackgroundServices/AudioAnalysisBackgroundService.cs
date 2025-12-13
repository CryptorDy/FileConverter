using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using FileConverter.Data;
using FileConverter.Models;
using FileConverter.Services.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using Newtonsoft.Json;
using Polly;

namespace FileConverter.Services
{
    /// <summary>
    /// Фоновый сервис для анализа аудио из очереди AudioAnalysisChannel.
    /// Работает после ConversionBackgroundService и анализирует MP3 файлы.
    /// </summary>
    public class AudioAnalysisBackgroundService : BackgroundService
    {
        private readonly ILogger<AudioAnalysisBackgroundService> _logger;
        private readonly IServiceProvider _serviceProvider;
        private readonly ProcessingChannels _channels;
        private readonly MetricsCollector _metricsCollector;
        private readonly CpuThrottleService _cpuThrottleService;
        private readonly int _maxConcurrentAnalyses;

        public AudioAnalysisBackgroundService(
            ILogger<AudioAnalysisBackgroundService> logger,
            IServiceProvider serviceProvider,
            ProcessingChannels channels,
            MetricsCollector metricsCollector,
            CpuThrottleService cpuThrottleService,
            IConfiguration configuration)
        {
            _logger = logger;
            _serviceProvider = serviceProvider;
            _channels = channels;
            _metricsCollector = metricsCollector;
            _cpuThrottleService = cpuThrottleService;
            // Количество параллельных анализов, по умолчанию равно количеству ядер CPU (минимум 1)
            _maxConcurrentAnalyses = configuration.GetValue<int>("Performance:MaxConcurrentAudioAnalyses", Math.Max(1, Environment.ProcessorCount));
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {

            var tasks = new Task[_maxConcurrentAnalyses];
            for (int i = 0; i < _maxConcurrentAnalyses; i++)
            {
                tasks[i] = Task.Run(() => WorkerLoop(stoppingToken), stoppingToken);
            }

            await Task.WhenAll(tasks);

            _logger.LogInformation("AudioAnalysisBackgroundService остановлен.");
        }

        private async Task WorkerLoop(CancellationToken stoppingToken)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                string jobId = string.Empty;
                string mp3Path = string.Empty;
                string videoPath = string.Empty;
                string videoHash = string.Empty;

                try
                {
                    // Ожидаем задачу из канала анализа аудио
                    var item = await _channels.AudioAnalysisChannel.Reader.ReadAsync(stoppingToken);
                    jobId = item.JobId;
                    mp3Path = item.Mp3Path;
                    videoPath = item.VideoPath;
                    videoHash = item.VideoHash;

                    _logger.LogInformation("AudioAnalysisWorker получил задачу {JobId} для анализа файла: {Mp3Path}, видео: {VideoPath}", 
                        (object)jobId, (object)mp3Path, (object)videoPath);

                    using var scope = _serviceProvider.CreateScope();
                    var jobRepository = scope.ServiceProvider.GetRequiredService<IJobRepository>();
                    var conversionLogger = scope.ServiceProvider.GetRequiredService<IConversionLogger>();
                    var tempFileManager = scope.ServiceProvider.GetRequiredService<ITempFileManager>();

                    // Пытаемся получить AudioAnalyzer, но если он недоступен - пропускаем анализ
                    AudioAnalyzer? audioAnalyzer = null;
                    try
                    {
                        audioAnalyzer = scope.ServiceProvider.GetRequiredService<AudioAnalyzer>();
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "AudioAnalyzer недоступен, пропускаем анализ аудио для задачи {JobId}", jobId);
                        await conversionLogger.LogWarningAsync(jobId, "Анализ аудио пропущен - библиотека Essentia недоступна", ex.Message);
                    }

                    DateTime analysisStart = DateTime.UtcNow;

                    try
                    {
                        var job = await jobRepository.GetJobByIdAsync(jobId);
                        if (job == null)
                        {
                            _logger.LogWarning("Задача {JobId} не найдена в репозитории после извлечения из очереди анализа аудио.", jobId);
                            await conversionLogger.LogErrorAsync(jobId, $"Задача {jobId} не найдена в БД.");
                            // Удаляем временный MP3 файл
                            CleanupFile(tempFileManager, mp3Path, _logger, jobId);
                            continue;
                        }

                        if (!File.Exists(mp3Path))
                        {
                            _logger.LogWarning("MP3 файл не найден для задачи {JobId}: {Mp3Path}", jobId, mp3Path);
                            await conversionLogger.LogErrorAsync(jobId, $"MP3 файл не найден: {mp3Path}");
                            await DbJobManager.UpdateJobStatusAsync(jobRepository, jobId, ConversionStatus.Failed, errorMessage: "MP3 файл не найден для анализа аудио");
                            continue;
                        }

                        // Если AudioAnalyzer доступен, выполняем анализ аудио
                        if (audioAnalyzer != null)
                        {
                            // Обновляем статус на AudioAnalyzing
                            await DbJobManager.UpdateJobStatusAsync(jobRepository, jobId, ConversionStatus.AudioAnalyzing);
                            await conversionLogger.LogStatusChangedAsync(jobId, ConversionStatus.AudioAnalyzing);
                            
                            await conversionLogger.LogSystemInfoAsync($"Задача {jobId}: Начинаем анализ аудио файла {mp3Path}");

                            // Запускаем таймер для метрик анализа аудио
                            _metricsCollector.StartTimer("audio_analysis", jobId);

                            _logger.LogInformation("Задача {JobId}: запуск анализа аудио с Essentia...", jobId);

                            // Настройка Polly retry policy для анализа аудио
                            var retryPolicy = Policy
                                .Handle<Exception>(ex => !(ex is OperationCanceledException && stoppingToken.IsCancellationRequested))
                                .WaitAndRetryAsync(
                                    retryCount: 2, // 2 попытки для анализа
                                    sleepDurationProvider: retryAttempt => TimeSpan.FromSeconds(3 * retryAttempt), // 3, 6 секунд
                                    onRetry: (outcome, timespan, retryCount, context) =>
                                    {
                                        _logger.LogWarning("Задача {JobId}: Попытка {RetryCount}/2 анализа аудио неудачна. Повтор через {Delay}с. Ошибка: {Error}", 
                                            jobId, retryCount, timespan.TotalSeconds, outcome?.Message ?? "Unknown");
                                    });

                            // Выполняем анализ аудио с retry
                            string analysisJsonResult = await retryPolicy.ExecuteAsync(async () =>
                            {
                                // Проверяем загрузку CPU перед началом анализа
                                await _cpuThrottleService.WaitIfNeededAsync(stoppingToken);
                                
                                _logger.LogInformation("Задача {JobId}: начинаем попытку анализа аудио", jobId);
                                
                                // Выполняем анализ аудио с защитой от сбоев native кода
                                string result;
                                
                                // Создаем CancellationTokenSource для тайм-аута
                                using var timeoutCts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
                                try
                                {
                                    // Добавляем тайм-аут для native вызова
                                    var task = Task.Run(() => audioAnalyzer.AnalyzeFromFile(mp3Path), timeoutCts.Token);
                                    result = await task;
                                }
                                catch (AccessViolationException avEx)
                                {
                                    throw new InvalidOperationException("Критическая ошибка в native библиотеке Essentia (AccessViolation)", avEx);
                                }
                                catch (SEHException sehEx)
                                {
                                    throw new InvalidOperationException("Ошибка Structured Exception Handling в native библиотеке Essentia", sehEx);
                                }
                                catch (Exception nativeEx) when (nativeEx.GetType().Name.Contains("External"))
                                {
                                    throw new InvalidOperationException($"Ошибка внешней библиотеки Essentia: {nativeEx.Message}", nativeEx);
                                }
                                catch (OperationCanceledException) when (timeoutCts.Token.IsCancellationRequested)
                                {
                                    throw new TimeoutException("Превышен тайм-аут анализа аудио (3 мин)");
                                }
                                
                                if (string.IsNullOrEmpty(result))
                                {
                                    throw new InvalidOperationException("Анализ аудио вернул пустой результат");
                                }
                                
                                _logger.LogInformation("Задача {JobId}: успешный анализ аудио", jobId);
                                return result;
                            });

                            // Останавливаем таймер для метрик (успешный анализ аудио)
                            _metricsCollector.StopTimer("audio_analysis", jobId, isSuccess: true);
                        
                        // Безопасная десериализация с использованием System.Text.Json
                        var analysisResponse = System.Text.Json.JsonSerializer.Deserialize<EssentiaAnalysisResponse>(analysisJsonResult);
                        
                        // Проверяем, не вернул ли Python-скрипт ошибку
                        if (!string.IsNullOrEmpty(analysisResponse?.Error))
                        {
                            throw new InvalidOperationException($"Ошибка в Python-скрипте Essentia: {analysisResponse.Error}");
                        }
                        
                        if (analysisResponse == null)
                        {
                            throw new InvalidOperationException("Не удалось десериализовать ответ Essentia");
                        }
                        
                        if (analysisResponse.AudioAnalysis == null)
                        {
                            throw new InvalidOperationException("Результат анализа аудио не содержит данных audio_analysis");
                        }

                        var audioAnalysis = analysisResponse.AudioAnalysis;

                        _logger.LogInformation("Задача {JobId}: анализ аудио завершен. BPM: {Bpm}, Confidence: {Confidence}, Beats: {Beats}", 
                            (object)jobId, (object)audioAnalysis.tempo_bpm, (object)audioAnalysis.confidence, (object)audioAnalysis.beats_detected);

                            // Сохраняем результат анализа в базу данных
                            job.AudioAnalysis = audioAnalysis;
                            await jobRepository.UpdateJobAsync(job);

                            long analysisTimeMs = (long)(DateTime.UtcNow - analysisStart).TotalMilliseconds;
                            
                            await conversionLogger.LogSystemInfoAsync($"Анализ аудио завершен для задания {jobId}. Время: {analysisTimeMs}мс. BPM: {audioAnalysis.tempo_bpm}");
                            
                            _logger.LogInformation("Задача {JobId}: анализ аудио успешно завершен и сохранен в БД.", jobId);
                        }
                        else
                        {
                            _logger.LogInformation("Задача {JobId}: анализ аудио пропущен (Essentia недоступна)", jobId);
                            await conversionLogger.LogSystemInfoAsync($"Задача {jobId}: Анализ аудио пропущен - библиотека Essentia недоступна");
                        }

                        // Передаем задачу дальше в очередь извлечения ключевых кадров
                        // Используем videoPath и videoHash, переданные через канал
                        if (!string.IsNullOrEmpty(videoPath) && File.Exists(videoPath))
                        {
                            bool keyframeQueueSuccess = _channels.KeyframeExtractionChannel.Writer.TryWrite((jobId, videoPath, mp3Path, videoHash));
                            if (keyframeQueueSuccess)
                            {
                                await conversionLogger.LogSystemInfoAsync($"Задание {jobId} передано в очередь извлечения ключевых кадров после анализа аудио");
                            }
                            else
                            {
                                _logger.LogWarning("Задача {JobId}: очередь извлечения ключевых кадров переполнена, файлы будут очищены", jobId);
                                await conversionLogger.LogErrorAsync(jobId, "Очередь извлечения ключевых кадров переполнена", null, ConversionStatus.Failed);
                                await DbJobManager.UpdateJobStatusAsync(jobRepository, jobId, ConversionStatus.Failed, errorMessage: "Очередь извлечения ключевых кадров переполнена");
                                // Очищаем временные файлы, так как они не будут обработаны дальше
                                CleanupFile(tempFileManager, mp3Path, _logger, jobId);
                            }
                        }
                        else
                        {
                            _logger.LogWarning("Задача {JobId}: видео файл не найден для передачи в очередь ключевых кадров. VideoPath: {VideoPath}", jobId, videoPath);
                            await conversionLogger.LogWarningAsync(jobId, $"Видео файл не найден для извлечения ключевых кадров: {videoPath}", "");
                            // Очищаем MP3 файл, так как видео недоступно
                            CleanupFile(tempFileManager, mp3Path, _logger, jobId);
                            await DbJobManager.UpdateJobStatusAsync(jobRepository, jobId, ConversionStatus.Failed, errorMessage: "Видео файл недоступен для извлечения ключевых кадров");
                        }

                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                    {
                        _logger.LogInformation("Обработка задачи {JobId} (анализ аудио) отменена.", jobId);
                        // Очищаем временный файл
                        CleanupFile(tempFileManager, mp3Path, _logger, jobId);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Задача {JobId}: Ошибка на этапе анализа аудио.", jobId);
                        
                        // Останавливаем таймер для метрик (неуспешный анализ аудио)
                        _metricsCollector.StopTimer("audio_analysis", jobId, isSuccess: false);
                        
                        await conversionLogger.LogErrorAsync(jobId, $"Ошибка при анализе аудио: {ex.Message}", ex.StackTrace, ConversionStatus.Failed);
                        await DbJobManager.UpdateJobStatusAsync(jobRepository, jobId, ConversionStatus.Failed, errorMessage: $"Ошибка анализа аудио: {ex.Message}");
                        // Стандартизированная очистка временных файлов
                        CleanupFile(tempFileManager, mp3Path, _logger, jobId);
                        // Также очищаем видео файл, если он есть (унификация с другими сервисами)
                        if (!string.IsNullOrEmpty(videoPath))
                        {
                            CleanupFile(tempFileManager, videoPath, _logger, jobId);
                        }
                    }
                } // Конец using scope
                catch (OperationCanceledException)
                {
                    _logger.LogInformation("AudioAnalysisWorker остановлен из-за токена отмены.");
                    break; // Выход из цикла while
                }
                catch (Exception ex)
                {
                    _logger.LogCritical(ex, "Критическая ошибка в AudioAnalysisBackgroundService WorkerLoop.");
                    await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
                }
            }
        }

        private void CleanupFile(ITempFileManager tempFileManager, string path, ILogger logger, string jobId)
        {
            if (!string.IsNullOrEmpty(path) && File.Exists(path))
            {
                try
                {
                    tempFileManager.DeleteTempFile(path);
                    logger.LogInformation("Задача {JobId}: Временный файл {Path} удален (этап анализа аудио).", jobId, path);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Задача {JobId}: Ошибка при удалении временного файла: {Path} (этап анализа аудио)", jobId, path);
                    // Логируем также через основной логгер задачи
                    using var cleanupScope = _serviceProvider.CreateScope();
                    var conversionLogger = cleanupScope.ServiceProvider.GetRequiredService<IConversionLogger>();
                    conversionLogger.LogWarningAsync(jobId, $"Ошибка при удалении временного файла после анализа аудио: {path}", ex.Message).GetAwaiter().GetResult();
                }
            }
        }

        public override Task StopAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("AudioAnalysisBackgroundService останавливается.");
            return base.StopAsync(cancellationToken);
        }
    }
} 