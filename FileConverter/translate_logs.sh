#!/bin/bash

# Script to replace Russian log messages with English ones
# Usage: chmod +x translate_logs.sh && ./translate_logs.sh

# Files to process
files=(
  "Program.cs"
  "Services/TempFileManager.cs"
  "Services/LocalStorageService.cs"
  "Services/MetricsCollector.cs"
  "Services/UrlValidator.cs"
  "Middleware/GlobalExceptionHandler.cs"
  "Services/FileConverterService.cs"
)

echo "Starting translation of log messages..."

# Program.cs replacements (already done through code edits)
echo "Program.cs - Already updated through previous edits"

# TempFileManager.cs replacements
echo "Processing TempFileManager.cs..."
if [ -f "Services/TempFileManager.cs" ]; then
  sed -i 's/Создан временный файл:/Created temporary file:/g' Services/TempFileManager.cs
  sed -i 's/размер:/size:/g' Services/TempFileManager.cs
  sed -i 's/КБ/KB/g' Services/TempFileManager.cs
  sed -i 's/Попытка удалить файл с пустым путем/Attempt to delete file with empty path/g' Services/TempFileManager.cs
  sed -i 's/Файл не существует:/File does not exist:/g' Services/TempFileManager.cs
  sed -i 's/Удален временный файл:/Temporary file deleted:/g' Services/TempFileManager.cs
  sed -i 's/Попытка удалить файл вне директории временных файлов:/Attempt to delete file outside temporary directory:/g' Services/TempFileManager.cs
  sed -i 's/Базовая директория:/Base directory:/g' Services/TempFileManager.cs
  sed -i 's/Отказано в доступе при удалении файла:/Access denied when deleting file:/g' Services/TempFileManager.cs
  sed -i 's/Проверьте права доступа./Check access rights./g' Services/TempFileManager.cs
  sed -i 's/Ошибка ввода-вывода при удалении файла:/IO error when deleting file:/g' Services/TempFileManager.cs
  sed -i 's/Возможно файл используется другим процессом./File may be used by another process./g' Services/TempFileManager.cs
  sed -i 's/Неизвестная ошибка при удалении временного файла:/Unknown error when deleting temporary file:/g' Services/TempFileManager.cs
  sed -i 's/Удален старый временный файл:/Deleted old temporary file:/g' Services/TempFileManager.cs
  sed -i 's/возраст:/age:/g' Services/TempFileManager.cs
  sed -i 's/ ч/ h/g' Services/TempFileManager.cs
  sed -i 's/Не удалось удалить временный файл:/Failed to delete temporary file:/g' Services/TempFileManager.cs
  sed -i 's/Удалена пустая директория:/Deleted empty directory:/g' Services/TempFileManager.cs
  sed -i 's/Не удалось удалить директорию:/Failed to delete directory:/g' Services/TempFileManager.cs
  sed -i 's/Очистка временных файлов: удалено/Temporary files cleanup: deleted/g' Services/TempFileManager.cs
  sed -i 's/файлов, освобождено/files, freed/g' Services/TempFileManager.cs
  sed -i 's/ МБ/ MB/g' Services/TempFileManager.cs
  sed -i 's/Ошибка при очистке временных файлов/Error while cleaning up temporary files/g' Services/TempFileManager.cs
  sed -i 's/Ошибка при получении статистики временных файлов/Error getting temporary files statistics/g' Services/TempFileManager.cs
  sed -i 's/Попытка доступа к файлу вне временной директории. Файл:/Attempt to access file outside of temporary directory. File:/g' Services/TempFileManager.cs
  sed -i 's/Временная директория:/Temp directory:/g' Services/TempFileManager.cs
  sed -i 's/Ошибка при проверке принадлежности файла к временной директории:/Error checking if file belongs to temporary directory:/g' Services/TempFileManager.cs
  echo "TempFileManager.cs - Done"
else
  echo "TempFileManager.cs - File not found"
fi

# LocalStorageService.cs replacements
echo "Processing LocalStorageService.cs..."
if [ -f "Services/LocalStorageService.cs" ]; then
  sed -i 's/Файл удален:/File deleted:/g' Services/LocalStorageService.cs
  sed -i 's/Файл для удаления не найден:/File to delete not found:/g' Services/LocalStorageService.cs
  sed -i 's/Невозможно удалить файл из внешнего источника:/Cannot delete file from external source:/g' Services/LocalStorageService.cs
  sed -i 's/Ошибка при удалении файла:/Error deleting file:/g' Services/LocalStorageService.cs
  echo "LocalStorageService.cs - Done"
else
  echo "LocalStorageService.cs - File not found"
fi

# UrlValidator.cs replacements
echo "Processing UrlValidator.cs..."
if [ -f "Services/UrlValidator.cs" ]; then
  sed -i 's/Получен пустой URL/Empty URL received/g' Services/UrlValidator.cs
  sed -i 's/Некорректный URL:/Invalid URL:/g' Services/UrlValidator.cs
  sed -i 's/Запрещен доступ к локальным адресам:/Access to local addresses forbidden:/g' Services/UrlValidator.cs
  sed -i 's/Потенциально опасный тип файла:/Potentially dangerous file type:/g' Services/UrlValidator.cs
  sed -i 's/Обнаружен URL социальной сети:/Social media URL detected:/g' Services/UrlValidator.cs
  sed -i 's/Возможно потребуются особые заголовки для доступа./Special headers may be required for access./g' Services/UrlValidator.cs
  echo "UrlValidator.cs - Done"
else
  echo "UrlValidator.cs - File not found"
fi

# MetricsCollector.cs replacements
echo "Processing MetricsCollector.cs..."
if [ -f "Services/MetricsCollector.cs" ]; then
  sed -i 's/Длительная операция:/Long operation:/g' Services/MetricsCollector.cs
  sed -i 's/Время:/Time:/g' Services/MetricsCollector.cs
  sed -i 's/Контекст:/Context:/g' Services/MetricsCollector.cs
  sed -i 's/Высокое использование ресурсов: CPU/High resource usage: CPU/g' Services/MetricsCollector.cs
  sed -i 's/Память:/Memory:/g' Services/MetricsCollector.cs
  echo "MetricsCollector.cs - Done"
else
  echo "MetricsCollector.cs - File not found"
fi

# GlobalExceptionHandler.cs replacements
echo "Processing GlobalExceptionHandler.cs..."
if [ -f "Middleware/GlobalExceptionHandler.cs" ]; then
  sed -i 's/Необработанное исключение:/Unhandled exception:/g' Middleware/GlobalExceptionHandler.cs
  sed -i 's/при обработке/when processing/g' Middleware/GlobalExceptionHandler.cs
  sed -i 's/КРИТИЧЕСКАЯ ОШИБКА:/CRITICAL ERROR:/g' Middleware/GlobalExceptionHandler.cs
  sed -i 's/Требуется немедленное вмешательство!/Immediate attention required!/g' Middleware/GlobalExceptionHandler.cs
  echo "GlobalExceptionHandler.cs - Done"
else
  echo "GlobalExceptionHandler.cs - File not found"
fi

# FileConverterService.cs replacements
echo "Processing FileConverterService.cs..."
if [ -f "Services/FileConverterService.cs" ]; then
  sed -i 's/Начало конвертации файла:/Starting file conversion:/g' Services/FileConverterService.cs
  sed -i 's/Файл уже конвертирован и найден в кэше:/File already converted and found in cache:/g' Services/FileConverterService.cs
  sed -i 's/Ошибка при конвертации файла:/Error converting file:/g' Services/FileConverterService.cs
  sed -i 's/Исходный формат:/Source format:/g' Services/FileConverterService.cs
  sed -i 's/Конвертация завершена успешно:/Conversion completed successfully:/g' Services/FileConverterService.cs
  sed -i 's/Время конвертации:/Conversion time:/g' Services/FileConverterService.cs
  echo "FileConverterService.cs - Done"
else
  echo "FileConverterService.cs - File not found"
fi

echo "Translation complete!" 