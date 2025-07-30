#pragma once

#ifdef ESSENTIAWRAPPER_EXPORTS
#define ESSENTIAWRAPPER_API __declspec(dllexport)
#else
#define ESSENTIAWRAPPER_API __declspec(dllimport)
#endif

// Структура для результатов анализа ритма
struct RhythmAnalysisResult {
    float bpm;
    float confidence;
    float* beat_timestamps;
    int beat_count;
    float* bpm_intervals;
    int interval_count;
};

extern "C" {
    // Инициализация библиотеки Essentia
    ESSENTIAWRAPPER_API int InitializeEssentia();
    
    // Освобождение ресурсов
    ESSENTIAWRAPPER_API void ShutdownEssentia();
    
    // Анализ ритма из аудиофайла
    ESSENTIAWRAPPER_API int AnalyzeRhythmFromFile(
        const char* audioFilePath,
        RhythmAnalysisResult* result
    );
    
    // Анализ ритма из массива сэмплов
    ESSENTIAWRAPPER_API int AnalyzeRhythmFromSamples(
        float* audioSamples,
        int sampleCount,
        int sampleRate,
        RhythmAnalysisResult* result
    );
    
    // Освобождение памяти результата
    ESSENTIAWRAPPER_API void FreeRhythmResult(RhythmAnalysisResult* result);
} 