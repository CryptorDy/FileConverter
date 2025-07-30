#!/usr/bin/env python3
"""
Скрипт для анализа аудио с использованием библиотеки Essentia
Используется FileConverter для извлечения ритмических характеристик из аудио файлов
"""

import sys
import json
import numpy as np
import os

try:
    import essentia.standard as es
    from essentia import Pool
except ImportError:
    print(json.dumps({"error": "Essentia не установлена"}))
    sys.exit(1)

def analyze_audio(audio_path):
    """
    Анализирует аудио файл и извлекает ритмические характеристики
    
    Args:
        audio_path (str): Путь к аудио файлу
        
    Returns:
        dict: Результат анализа или ошибка
    """
    try:
        if not os.path.exists(audio_path):
            return {"error": f"Аудио файл не найден: {audio_path}"}
            
        # Загружаем аудио файл
        loader = es.MonoLoader(filename=audio_path)
        audio = loader()
        
        if len(audio) == 0:
            return {"error": "Аудио файл пуст или поврежден"}
        
        # Извлекаем темп и биты
        rhythm_extractor = es.RhythmExtractor2013(method="multifeature")
        bpm, beats, beats_confidence, _, beats_intervals = rhythm_extractor(audio)
        
        # Конвертируем beats в секунды
        beat_timestamps_sec = [float(beat) for beat in beats]
        
        # Вычисляем regularity
        if len(beats_intervals) > 1:
            rhythm_regularity = float(1.0 - np.std(beats_intervals) / np.mean(beats_intervals))
            rhythm_regularity = max(0.0, min(1.0, rhythm_regularity))  # Ограничиваем от 0 до 1
        else:
            rhythm_regularity = 0.0
            
        result = {
            "tempo_bpm": float(bpm),
            "confidence": float(beats_confidence),
            "beat_timestamps_sec": beat_timestamps_sec,
            "bpm_intervals": [float(interval) for interval in beats_intervals],
            "beats_detected": len(beats),
            "rhythm_regularity": rhythm_regularity
        }
        
        return {"audio_analysis": result}
        
    except Exception as e:
        return {"error": f"Ошибка анализа аудио: {str(e)}"}

def test_essentia():
    """
    Тестирует доступность библиотеки Essentia
    
    Returns:
        dict: Статус теста
    """
    try:
        # Простой тест доступности Essentia
        _ = es.MonoLoader()
        _ = es.RhythmExtractor2013()
        return {"status": "ok", "message": "Essentia доступна"}
    except Exception as e:
        return {"error": f"Essentia недоступна: {str(e)}"}

def main():
    """Главная функция скрипта"""
    if len(sys.argv) < 2:
        print(json.dumps({"error": "Использование: python3 essentia_analyzer.py <audio_file_path> или --test"}))
        sys.exit(1)
    
    if sys.argv[1] == "--test":
        result = test_essentia()
    else:
        audio_path = sys.argv[1]
        result = analyze_audio(audio_path)
    
    print(json.dumps(result, ensure_ascii=False))

if __name__ == "__main__":
    main() 