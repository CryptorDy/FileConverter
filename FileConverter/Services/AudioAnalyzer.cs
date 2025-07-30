using System;
using System.Diagnostics;
using System.Text;
using System.IO;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Microsoft.Extensions.Logging;

namespace FileConverter.Services
{
    /// <summary>
    /// Сервис для анализа аудио с использованием библиотеки Essentia через Python
    /// </summary>
    public class AudioAnalyzer : IDisposable
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

        private readonly ILogger<AudioAnalyzer> _logger;
        private bool _initialized = false;
        private bool _disposed = false;
        private string _pythonScriptPath;

        public AudioAnalyzer(ILogger<AudioAnalyzer> logger)
        {
            _logger = logger;
            _pythonScriptPath = string.Empty;
            
            try
            {
                InitializeEssentia();
                _initialized = true;
                _logger.LogInformation("AudioAnalyzer успешно инициализирован");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ошибка при инициализации AudioAnalyzer");
                throw;
            }
        }

        private void InitializeEssentia()
        {
            // Сначала пытаемся использовать скрипт из основной директории
            var primaryScriptPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Scripts", "essentia_analyzer.py");
            
            if (File.Exists(primaryScriptPath))
            {
                // Используем скрипт из основной директории
                _pythonScriptPath = primaryScriptPath;
            }
            else
            {
                // Создаем копию во временной директории
                _pythonScriptPath = Path.Combine(Path.GetTempPath(), "essentia_analyzer.py");
                CreatePythonScript();
            }
            
            // Проверяем доступность Python и Essentia
            var testResult = RunPythonScript("--test");
            if (!string.IsNullOrEmpty(testResult) && testResult.Contains("error"))
            {
                throw new Exception($"Essentia недоступна: {testResult}");
            }
        }

        private void CreatePythonScript()
        {
            // Копируем готовый Python скрипт из ресурсов приложения
            var sourceScriptPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Scripts", "essentia_analyzer.py");
            
            if (File.Exists(sourceScriptPath))
            {
                File.Copy(sourceScriptPath, _pythonScriptPath, overwrite: true);
            }
            else
            {
                throw new FileNotFoundException($"Python скрипт не найден: {sourceScriptPath}");
            }
            
            // Делаем скрипт исполняемым на Linux
            if (Environment.OSVersion.Platform == PlatformID.Unix)
            {
                var chmod = new ProcessStartInfo("chmod", $"+x {_pythonScriptPath}")
                {
                    UseShellExecute = false
                };
                Process.Start(chmod)?.WaitForExit();
            }
        }

        public string AnalyzeFromFile(string audioFilePath)
        {
            if (!_initialized)
            {
                throw new InvalidOperationException("AudioAnalyzer не инициализирован");
            }

            if (!File.Exists(audioFilePath))
            {
                throw new FileNotFoundException($"Аудио файл не найден: {audioFilePath}");
            }

            try
            {
                _logger.LogDebug("Начинаем анализ аудио файла: {AudioFilePath}", audioFilePath);
                
                var result = RunPythonScript(audioFilePath);
                
                _logger.LogDebug("Анализ аудио завершен, результат: {Result}", result);
                
                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ошибка при анализе аудио файла: {AudioFilePath}", audioFilePath);
                throw;
            }
        }

        private string RunPythonScript(string argument)
        {
            // Пытаемся использовать Python из виртуального окружения, если доступно
            var pythonExecutable = GetPythonExecutable();
            
            var startInfo = new ProcessStartInfo
            {
                FileName = pythonExecutable,
                Arguments = $"{_pythonScriptPath} {argument}",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            
            // Добавляем переменные окружения для Python
            var pythonPath = Environment.GetEnvironmentVariable("PYTHONPATH");
            if (!string.IsNullOrEmpty(pythonPath))
            {
                startInfo.EnvironmentVariables["PYTHONPATH"] = pythonPath;
            }

            using var process = Process.Start(startInfo);
            if (process == null)
            {
                throw new InvalidOperationException("Не удалось запустить Python процесс");
            }

            var output = process.StandardOutput.ReadToEnd();
            var error = process.StandardError.ReadToEnd();
            
            process.WaitForExit();

            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException($"Python скрипт завершился с ошибкой: {error}");
            }

            if (!string.IsNullOrEmpty(error))
            {
                _logger.LogWarning("Python предупреждения: {Error}", error);
            }

            return output.Trim();
        }

        private string GetPythonExecutable()
        {
            // Порядок приоритета для поиска Python
            var pythonCandidates = new[]
            {
                "/opt/essentia-venv/bin/python3",     // Виртуальное окружение (альтернативный Dockerfile)
                "/opt/venv/bin/python3",              // Виртуальное окружение (основной Dockerfile)
                "/usr/local/bin/python3-essentia",   // Symlink
                "python3"                             // Системный Python
            };

            foreach (var candidate in pythonCandidates)
            {
                if (File.Exists(candidate) || candidate == "python3")
                {
                    _logger.LogDebug("Используем Python: {PythonPath}", candidate);
                    return candidate;
                }
            }

            _logger.LogWarning("Не найден подходящий Python исполняемый файл, используем python3 по умолчанию");
            return "python3";
        }

        public void Dispose()
        {
            if (_disposed) return;

            try
            {
                // Удаляем только временный Python скрипт (не из основной директории)
                if (File.Exists(_pythonScriptPath) && _pythonScriptPath.Contains(Path.GetTempPath()))
                {
                    File.Delete(_pythonScriptPath);
                }
                
                _logger.LogInformation("AudioAnalyzer ресурсы освобождены");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Ошибка при освобождении ресурсов AudioAnalyzer");
            }
            finally
            {
                _disposed = true;
            }
        }
    }
} 