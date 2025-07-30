#include "pch.h"
#include "EssentiaWrapper.h"
#include <essentia/algorithmfactory.h>
#include <essentia/essentiamath.h>
#include <essentia/scheduler/network.h>
#include <essentia/streaming/algorithms/poolstorage.h>

using namespace essentia;
using namespace essentia::standard;

static bool g_initialized = false;

extern "C" ESSENTIAWRAPPER_API int InitializeEssentia() {
    if (g_initialized) return 1;
    
    try {
        essentia::init();
        g_initialized = true;
        return 1;
    }
    catch (...) {
        return 0;
    }
}

extern "C" ESSENTIAWRAPPER_API void ShutdownEssentia() {
    if (g_initialized) {
        essentia::shutdown();
        g_initialized = false;
    }
}

extern "C" ESSENTIAWRAPPER_API int AnalyzeRhythmFromFile(
    const char* audioFilePath,
    RhythmAnalysisResult* result) {
    
    if (!g_initialized || !audioFilePath || !result) return 0;
    
    try {
        // Инициализация алгоритмов
        Algorithm* audioLoader = AlgorithmFactory::create("MonoLoader",
                                                         "filename", audioFilePath,
                                                         "sampleRate", 44100);
        Algorithm* rhythmExtractor = AlgorithmFactory::create("RhythmExtractor2013",
                                                            "method", "multifeature");
        
        // Переменные для результатов
        std::vector<Real> audioBuffer;
        Real bpm;
        std::vector<Real> ticks;
        Real confidence;
        std::vector<Real> estimates;
        std::vector<Real> bpmIntervals;
        
        // Подключение алгоритмов
        audioLoader->output("audio").set(audioBuffer);
        
        rhythmExtractor->input("signal").set(audioBuffer);
        rhythmExtractor->output("bpm").set(bpm);
        rhythmExtractor->output("ticks").set(ticks);
        rhythmExtractor->output("confidence").set(confidence);
        rhythmExtractor->output("estimates").set(estimates);
        rhythmExtractor->output("bpmIntervals").set(bpmIntervals);
        
        // Выполнение анализа
        audioLoader->compute();
        rhythmExtractor->compute();
        
        // Заполнение результатов
        result->bpm = bpm;
        result->confidence = confidence;
        result->beat_count = static_cast<int>(ticks.size());
        result->interval_count = static_cast<int>(bpmIntervals.size());
        
        // Копирование массивов
        if (result->beat_count > 0) {
            result->beat_timestamps = new float[result->beat_count];
            for (int i = 0; i < result->beat_count; i++) {
                result->beat_timestamps[i] = static_cast<float>(ticks[i]);
            }
        }
        
        if (result->interval_count > 0) {
            result->bpm_intervals = new float[result->interval_count];
            for (int i = 0; i < result->interval_count; i++) {
                result->bpm_intervals[i] = static_cast<float>(bpmIntervals[i]);
            }
        }
        
        // Очистка алгоритмов
        delete audioLoader;
        delete rhythmExtractor;
        
        return 1;
    }
    catch (...) {
        return 0;
    }
}

extern "C" ESSENTIAWRAPPER_API int AnalyzeRhythmFromSamples(
    float* audioSamples,
    int sampleCount,
    int sampleRate,
    RhythmAnalysisResult* result) {
    
    if (!g_initialized || !audioSamples || sampleCount <= 0 || !result) return 0;
    
    try {
        // Конвертация в вектор Essentia
        std::vector<Real> audioVector;
        audioVector.reserve(sampleCount);
        
        for (int i = 0; i < sampleCount; i++) {
            audioVector.push_back(static_cast<Real>(audioSamples[i]));
        }
        
        // Ресэмплинг до 44100 Hz если необходимо
        if (sampleRate != 44100) {
            Algorithm* resampler = AlgorithmFactory::create("Resample",
                                                          "inputSampleRate", sampleRate,
                                                          "outputSampleRate", 44100);
            
            std::vector<Real> resampledAudio;
            resampler->input("signal").set(audioVector);
            resampler->output("signal").set(resampledAudio);
            resampler->compute();
            
            audioVector = resampledAudio;
            delete resampler;
        }
        
        // Создание RhythmExtractor2013
        Algorithm* rhythmExtractor = AlgorithmFactory::create("RhythmExtractor2013",
                                                            "method", "multifeature");
        
        // Переменные для результатов
        Real bpm;
        std::vector<Real> ticks;
        Real confidence;
        std::vector<Real> estimates;
        std::vector<Real> bpmIntervals;
        
        // Подключение входов и выходов
        rhythmExtractor->input("signal").set(audioVector);
        rhythmExtractor->output("bpm").set(bpm);
        rhythmExtractor->output("ticks").set(ticks);
        rhythmExtractor->output("confidence").set(confidence);
        rhythmExtractor->output("estimates").set(estimates);
        rhythmExtractor->output("bpmIntervals").set(bpmIntervals);
        
        // Выполнение анализа
        rhythmExtractor->compute();
        
        // Заполнение результатов (аналогично предыдущей функции)
        result->bpm = bpm;
        result->confidence = confidence;
        result->beat_count = static_cast<int>(ticks.size());
        result->interval_count = static_cast<int>(bpmIntervals.size());
        
        if (result->beat_count > 0) {
            result->beat_timestamps = new float[result->beat_count];
            for (int i = 0; i < result->beat_count; i++) {
                result->beat_timestamps[i] = static_cast<float>(ticks[i]);
            }
        }
        
        if (result->interval_count > 0) {
            result->bpm_intervals = new float[result->interval_count];
            for (int i = 0; i < result->interval_count; i++) {
                result->bpm_intervals[i] = static_cast<float>(bpmIntervals[i]);
            }
        }
        
        delete rhythmExtractor;
        return 1;
    }
    catch (...) {
        return 0;
    }
}

extern "C" ESSENTIAWRAPPER_API void FreeRhythmResult(RhythmAnalysisResult* result) {
    if (result) {
        delete[] result->beat_timestamps;
        delete[] result->bpm_intervals;
        result->beat_timestamps = nullptr;
        result->bpm_intervals = nullptr;
        result->beat_count = 0;
        result->interval_count = 0;
    }
} 