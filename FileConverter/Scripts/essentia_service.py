#!/usr/bin/env python3
"""
HTTP-сервис для анализа аудио с использованием библиотеки Essentia
Предоставляет REST API для FileConverter
"""

import sys
import json
import numpy as np
import os
from http.server import HTTPServer, BaseHTTPRequestHandler
from urllib.parse import urlparse, parse_qs
import threading
import traceback

try:
    import essentia.standard as es
    from essentia import Pool
    ESSENTIA_AVAILABLE = True
except ImportError:
    ESSENTIA_AVAILABLE = False

class EssentiaHandler(BaseHTTPRequestHandler):
    def do_GET(self):
        """Обработка GET запросов"""
        try:
            parsed_path = urlparse(self.path)
            
            if parsed_path.path == '/health':
                self.send_health_check()
            elif parsed_path.path == '/analyze':
                query_params = parse_qs(parsed_path.query)
                audio_path = query_params.get('file', [None])[0]
                
                if not audio_path:
                    self.send_error_response(400, "Параметр 'file' обязателен")
                    return
                    
                result = self.analyze_audio(audio_path)
                self.send_json_response(result)
            else:
                self.send_error_response(404, "Эндпоинт не найден")
                
        except Exception as e:
            self.log_error(f"Ошибка при обработке GET запроса: {str(e)}")
            self.send_error_response(500, f"Внутренняя ошибка сервера: {str(e)}")

    def do_POST(self):
        """Обработка POST запросов"""
        try:
            if self.path == '/analyze':
                content_length = int(self.headers['Content-Length'])
                post_data = self.rfile.read(content_length)
                
                try:
                    data = json.loads(post_data.decode('utf-8'))
                    audio_path = data.get('file_path')
                    
                    if not audio_path:
                        self.send_error_response(400, "Поле 'file_path' обязательно")
                        return
                        
                    result = self.analyze_audio(audio_path)
                    self.send_json_response(result)
                    
                except json.JSONDecodeError:
                    self.send_error_response(400, "Неверный JSON формат")
            else:
                self.send_error_response(404, "Эндпоинт не найден")
                
        except Exception as e:
            self.log_error(f"Ошибка при обработке POST запроса: {str(e)}")
            self.send_error_response(500, f"Внутренняя ошибка сервера: {str(e)}")

    def send_health_check(self):
        """Отправляет ответ о состоянии сервиса"""
        if not ESSENTIA_AVAILABLE:
            self.send_error_response(503, "Essentia недоступна")
            return
            
        try:
            # Тестируем Essentia
            _ = es.MonoLoader()
            _ = es.RhythmExtractor2013()
            
            health_data = {
                "status": "healthy",
                "essentia_available": True,
                "message": "Сервис Essentia работает корректно"
            }
            self.send_json_response(health_data)
            
        except Exception as e:
            self.send_error_response(503, f"Essentia недоступна: {str(e)}")

    def analyze_audio(self, audio_path):
        """
        Анализирует аудио файл и возвращает ритмические характеристики
        
        Args:
            audio_path (str): Путь к аудио файлу
            
        Returns:
            dict: Результат анализа или ошибка
        """
        if not ESSENTIA_AVAILABLE:
            return {"error": "Essentia не установлена"}
            
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
            error_msg = f"Ошибка анализа аудио: {str(e)}"
            self.log_error(f"{error_msg}\n{traceback.format_exc()}")
            return {"error": error_msg}

    def send_json_response(self, data, status_code=200):
        """Отправляет JSON ответ"""
        self.send_response(status_code)
        self.send_header('Content-type', 'application/json')
        self.send_header('Access-Control-Allow-Origin', '*')
        self.end_headers()
        
        json_data = json.dumps(data, ensure_ascii=False)
        self.wfile.write(json_data.encode('utf-8'))

    def send_error_response(self, status_code, message):
        """Отправляет ответ об ошибке"""
        error_data = {"error": message}
        self.send_json_response(error_data, status_code)

    def log_message(self, format, *args):
        """Переопределяем стандартное логирование"""
        print(f"[{self.log_date_time_string()}] {format % args}")

def run_server():
    """Запускает HTTP сервер"""
    port = int(os.environ.get('ESSENTIA_PORT', 8080))
    server_address = ('', port)
    
    httpd = HTTPServer(server_address, EssentiaHandler)
    
    print(f"Запускаем Essentia HTTP сервис на порту {port}")
    print(f"Essentia доступна: {ESSENTIA_AVAILABLE}")
    
    if ESSENTIA_AVAILABLE:
        print("Доступные эндпоинты:")
        print("  GET  /health - проверка состояния сервиса")
        print("  GET  /analyze?file=<path> - анализ аудио файла")
        print("  POST /analyze - анализ аудио файла (JSON: {'file_path': '<path>'})")
    else:
        print("ВНИМАНИЕ: Essentia недоступна!")
    
    try:
        httpd.serve_forever()
    except KeyboardInterrupt:
        print("\nОстанавливаем сервер...")
        httpd.shutdown()

if __name__ == "__main__":
    run_server() 