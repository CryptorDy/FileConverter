using System;
using System.Runtime.InteropServices;
using Newtonsoft.Json;
using Microsoft.Extensions.Logging;

namespace FileConverter.Services
{
    /// <summary>
    /// Сервис для анализа аудио с использованием библиотеки Essentia
    /// </summary>
    public class AudioAnalyzer : IDisposable
    {
        [StructLayout(LayoutKind.Sequential)]
        public struct RhythmAnalysisResult
        {
            public float bpm;
            public float confidence;
            public IntPtr beat_timestamps;
            public int beat_count;
            public IntPtr bpm_intervals;
            public int interval_count;
        }

        // Для Linux (Docker)
        [DllImport("libEssentiaWrapper.so", CallingConvention = CallingConvention.Cdecl)]
        private static extern int InitializeEssentia();

        [DllImport("libEssentiaWrapper.so", CallingConvention = CallingConvention.Cdecl)]
        private static extern void ShutdownEssentia();

        [DllImport("libEssentiaWrapper.so", CallingConvention = CallingConvention.Cdecl)]
        private static extern int AnalyzeRhythmFromFile(
            [MarshalAs(UnmanagedType.LPStr)] string audioFilePath,
            out RhythmAnalysisResult result);

        [DllImport("libEssentiaWrapper.so", CallingConvention = CallingConvention.Cdecl)]
        private static extern int AnalyzeRhythmFromSamples(
            float[] audioSamples,
            int sampleCount,
            int sampleRate,
            out RhythmAnalysisResult result);

        [DllImport("libEssentiaWrapper.so", CallingConvention = CallingConvention.Cdecl)]
        private static extern void FreeRhythmResult(ref RhythmAnalysisResult result);

        public class AudioAnalysis
        {
            public float tempo_bpm { get; set; }
            public float confidence { get; set; }
            public float[] beat_timestamps_sec { get; set; } = Array.Empty<float>();
            public float[] bpm_intervals { get; set; } = Array.Empty<float>();
            public int beats_detected { get; set; }
            public double rhythm_regularity { get; set; }
        }

        private readonly ILogger<AudioAnalyzer> _logger;
        private bool _initialized = false;
        private bool _disposed = false;

        public AudioAnalyzer(ILogger<AudioAnalyzer> logger)
        {
            _logger = logger;
            
            try
            {
                if (InitializeEssentia() == 0)
                {
                    throw new Exception("Не удалось инициализировать Essentia");
                }
                _initialized = true;
                _logger.LogInformation("AudioAnalyzer успешно инициализирован");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ошибка при инициализации AudioAnalyzer");
                throw;
            }
        }

        public string AnalyzeFromFile(string audioFilePath)
        {
            if (!_initialized)
            {
                return CreateErrorJson("Анализатор не инициализирован");
            }

            if (string.IsNullOrEmpty(audioFilePath) || !File.Exists(audioFilePath))
            {
                return CreateErrorJson($"Аудиофайл не найден: {audioFilePath}");
            }

            try
            {
                _logger.LogInformation("Начинаем анализ аудио файла: {AudioFile}", audioFilePath);
                
                RhythmAnalysisResult nativeResult;
                
                int success = AnalyzeRhythmFromFile(audioFilePath, out nativeResult);
                if (success == 0)
                {
                    _logger.LogWarning("Не удалось проанализировать аудиофайл: {AudioFile}", audioFilePath);
                    return CreateErrorJson("Не удалось проанализировать аудиофайл");
                }

                var analysis = ConvertToManagedResult(nativeResult);
                
                // Освобождение памяти
                FreeRhythmResult(ref nativeResult);
                
                _logger.LogInformation("Анализ аудио завершен. BPM: {Bpm}, Confidence: {Confidence}, Beats: {Beats}", 
                    analysis.tempo_bpm, analysis.confidence, analysis.beats_detected);
                
                var audioAnalysisJson = new
                {
                    source_file = audioFilePath,
                    audio_analysis = analysis
                };

                return JsonConvert.SerializeObject(audioAnalysisJson, Formatting.Indented);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ошибка при анализе аудиофайла: {AudioFile}", audioFilePath);
                return CreateErrorJson($"Ошибка анализа: {ex.Message}");
            }
        }

        public string AnalyzeFromSamples(float[] audioSamples, int sampleRate)
        {
            if (!_initialized)
            {
                return CreateErrorJson("Анализатор не инициализирован");
            }

            if (audioSamples == null || audioSamples.Length == 0)
            {
                return CreateErrorJson("Массив аудиосэмплов пуст");
            }

            try
            {
                _logger.LogInformation("Начинаем анализ аудио из сэмплов. Count: {Count}, SampleRate: {SampleRate}", 
                    audioSamples.Length, sampleRate);
                
                RhythmAnalysisResult nativeResult;
                
                int success = AnalyzeRhythmFromSamples(audioSamples, audioSamples.Length, sampleRate, out nativeResult);
                if (success == 0)
                {
                    _logger.LogWarning("Не удалось проанализировать аудиосэмплы");
                    return CreateErrorJson("Не удалось проанализировать аудиосэмплы");
                }

                var analysis = ConvertToManagedResult(nativeResult);
                
                // Освобождение памяти
                FreeRhythmResult(ref nativeResult);
                
                _logger.LogInformation("Анализ аудио завершен. BPM: {Bpm}, Confidence: {Confidence}, Beats: {Beats}", 
                    analysis.tempo_bpm, analysis.confidence, analysis.beats_detected);
                
                var audioAnalysisJson = new
                {
                    sample_rate = sampleRate,
                    sample_count = audioSamples.Length,
                    audio_analysis = analysis
                };

                return JsonConvert.SerializeObject(audioAnalysisJson, Formatting.Indented);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ошибка при анализе аудиосэмплов");
                return CreateErrorJson($"Ошибка анализа: {ex.Message}");
            }
        }

        private AudioAnalysis ConvertToManagedResult(RhythmAnalysisResult nativeResult)
        {
            var analysis = new AudioAnalysis
            {
                tempo_bpm = nativeResult.bpm,
                confidence = nativeResult.confidence,
                beats_detected = nativeResult.beat_count
            };

            // Копирование массива временных меток битов
            if (nativeResult.beat_count > 0 && nativeResult.beat_timestamps != IntPtr.Zero)
            {
                analysis.beat_timestamps_sec = new float[nativeResult.beat_count];
                Marshal.Copy(nativeResult.beat_timestamps, analysis.beat_timestamps_sec, 0, nativeResult.beat_count);
            }

            // Копирование интервалов BPM
            if (nativeResult.interval_count > 0 && nativeResult.bpm_intervals != IntPtr.Zero)
            {
                analysis.bpm_intervals = new float[nativeResult.interval_count];
                Marshal.Copy(nativeResult.bpm_intervals, analysis.bpm_intervals, 0, nativeResult.interval_count);
            }

            // Вычисление регулярности ритма
            analysis.rhythm_regularity = CalculateRhythmRegularity(analysis.beat_timestamps_sec);

            return analysis;
        }

        private double CalculateRhythmRegularity(float[] beatTimestamps)
        {
            if (beatTimestamps.Length < 2) return 0.0;

            // Вычисление интервалов между битами
            var intervals = new double[beatTimestamps.Length - 1];
            for (int i = 1; i < beatTimestamps.Length; i++)
            {
                intervals[i - 1] = beatTimestamps[i] - beatTimestamps[i - 1];
            }

            // Вычисление стандартного отклонения
            double mean = 0;
            foreach (var interval in intervals)
            {
                mean += interval;
            }
            mean /= intervals.Length;

            double variance = 0;
            foreach (var interval in intervals)
            {
                variance += Math.Pow(interval - mean, 2);
            }
            variance /= intervals.Length;

            double stdDev = Math.Sqrt(variance);
            
            // Регулярность как обратная величина коэффициента вариации
            if (mean == 0) return 0.0;
            
            double coeffVar = stdDev / mean;
            return Math.Max(0.0, Math.Min(1.0, 1.0 - coeffVar));
        }

        private string CreateErrorJson(string message)
        {
            return JsonConvert.SerializeObject(new { error = message }, Formatting.Indented);
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (_initialized)
                {
                    try
                    {
                        ShutdownEssentia();
                        _logger.LogInformation("AudioAnalyzer корректно завершен");
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Ошибка при завершении работы AudioAnalyzer");
                    }
                    _initialized = false;
                }
                _disposed = true;
            }
        }

        ~AudioAnalyzer()
        {
            Dispose(false);
        }
    }
} 