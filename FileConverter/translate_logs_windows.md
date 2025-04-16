# Инструкция по переводу логов на английский (Windows)

Эта инструкция поможет заменить русские сообщения логирования на английские в проекте FileConverter.

## Инструкция

1. **Установка PowerShell Core** (если не установлен):
   - Скачайте и установите PowerShell Core с [официального сайта](https://github.com/PowerShell/PowerShell/releases)

2. **Выполните следующий скрипт в PowerShell**:

```powershell
# Скрипт для замены русских сообщений логирования на английские

# Файлы для обработки
$files = @(
  "Program.cs",
  "Services\TempFileManager.cs",
  "Services\LocalStorageService.cs",
  "Services\MetricsCollector.cs",
  "Services\UrlValidator.cs",
  "Middleware\GlobalExceptionHandler.cs",
  "Services\FileConverterService.cs"
)

Write-Host "Начало перевода сообщений логирования..." -ForegroundColor Green

# Program.cs уже обновлен через предыдущие правки
Write-Host "Program.cs - Уже обновлен через предыдущие правки" -ForegroundColor Cyan

# TempFileManager.cs замены
Write-Host "Обработка TempFileManager.cs..." -ForegroundColor Cyan
if (Test-Path "Services\TempFileManager.cs") {
  $content = Get-Content "Services\TempFileManager.cs" -Raw
  $content = $content -replace "Создан временный файл:", "Created temporary file:"
  $content = $content -replace "размер:", "size:"
  $content = $content -replace "КБ", "KB"
  $content = $content -replace "Попытка удалить файл с пустым путем", "Attempt to delete file with empty path"
  $content = $content -replace "Файл не существует:", "File does not exist:"
  $content = $content -replace "Удален временный файл:", "Temporary file deleted:"
  $content = $content -replace "Попытка удалить файл вне директории временных файлов:", "Attempt to delete file outside temporary directory:"
  $content = $content -replace "Базовая директория:", "Base directory:"
  $content = $content -replace "Отказано в доступе при удалении файла:", "Access denied when deleting file:"
  $content = $content -replace "Проверьте права доступа.", "Check access rights."
  $content = $content -replace "Ошибка ввода-вывода при удалении файла:", "IO error when deleting file:"
  $content = $content -replace "Возможно файл используется другим процессом.", "File may be used by another process."
  $content = $content -replace "Неизвестная ошибка при удалении временного файла:", "Unknown error when deleting temporary file:"
  $content = $content -replace "Удален старый временный файл:", "Deleted old temporary file:"
  $content = $content -replace "возраст:", "age:"
  $content = $content -replace " ч", " h"
  $content = $content -replace "Не удалось удалить временный файл:", "Failed to delete temporary file:"
  $content = $content -replace "Удалена пустая директория:", "Deleted empty directory:"
  $content = $content -replace "Не удалось удалить директорию:", "Failed to delete directory:"
  $content = $content -replace "Очистка временных файлов: удалено", "Temporary files cleanup: deleted"
  $content = $content -replace "файлов, освобождено", "files, freed"
  $content = $content -replace " МБ", " MB"
  $content = $content -replace "Ошибка при очистке временных файлов", "Error while cleaning up temporary files"
  $content = $content -replace "Ошибка при получении статистики временных файлов", "Error getting temporary files statistics"
  $content = $content -replace "Попытка доступа к файлу вне временной директории. Файл:", "Attempt to access file outside of temporary directory. File:"
  $content = $content -replace "Временная директория:", "Temp directory:"
  $content = $content -replace "Ошибка при проверке принадлежности файла к временной директории:", "Error checking if file belongs to temporary directory:"
  Set-Content "Services\TempFileManager.cs" $content
  Write-Host "TempFileManager.cs - Готово" -ForegroundColor Green
} else {
  Write-Host "TempFileManager.cs - Файл не найден" -ForegroundColor Red
}

# LocalStorageService.cs замены
Write-Host "Обработка LocalStorageService.cs..." -ForegroundColor Cyan
if (Test-Path "Services\LocalStorageService.cs") {
  $content = Get-Content "Services\LocalStorageService.cs" -Raw
  $content = $content -replace "Файл удален:", "File deleted:"
  $content = $content -replace "Файл для удаления не найден:", "File to delete not found:"
  $content = $content -replace "Невозможно удалить файл из внешнего источника:", "Cannot delete file from external source:"
  $content = $content -replace "Ошибка при удалении файла:", "Error deleting file:"
  Set-Content "Services\LocalStorageService.cs" $content
  Write-Host "LocalStorageService.cs - Готово" -ForegroundColor Green
} else {
  Write-Host "LocalStorageService.cs - Файл не найден" -ForegroundColor Red
}

# UrlValidator.cs замены
Write-Host "Обработка UrlValidator.cs..." -ForegroundColor Cyan
if (Test-Path "Services\UrlValidator.cs") {
  $content = Get-Content "Services\UrlValidator.cs" -Raw
  $content = $content -replace "Получен пустой URL", "Empty URL received"
  $content = $content -replace "Некорректный URL:", "Invalid URL:"
  $content = $content -replace "Запрещен доступ к локальным адресам:", "Access to local addresses forbidden:"
  $content = $content -replace "Потенциально опасный тип файла:", "Potentially dangerous file type:"
  $content = $content -replace "Обнаружен URL социальной сети:", "Social media URL detected:"
  $content = $content -replace "Возможно потребуются особые заголовки для доступа.", "Special headers may be required for access."
  Set-Content "Services\UrlValidator.cs" $content
  Write-Host "UrlValidator.cs - Готово" -ForegroundColor Green
} else {
  Write-Host "UrlValidator.cs - Файл не найден" -ForegroundColor Red
}

# MetricsCollector.cs замены
Write-Host "Обработка MetricsCollector.cs..." -ForegroundColor Cyan
if (Test-Path "Services\MetricsCollector.cs") {
  $content = Get-Content "Services\MetricsCollector.cs" -Raw
  $content = $content -replace "Длительная операция:", "Long operation:"
  $content = $content -replace "Время:", "Time:"
  $content = $content -replace "Контекст:", "Context:"
  $content = $content -replace "Высокое использование ресурсов: CPU", "High resource usage: CPU"
  $content = $content -replace "Память:", "Memory:"
  Set-Content "Services\MetricsCollector.cs" $content
  Write-Host "MetricsCollector.cs - Готово" -ForegroundColor Green
} else {
  Write-Host "MetricsCollector.cs - Файл не найден" -ForegroundColor Red
}

# GlobalExceptionHandler.cs замены
Write-Host "Обработка GlobalExceptionHandler.cs..." -ForegroundColor Cyan
if (Test-Path "Middleware\GlobalExceptionHandler.cs") {
  $content = Get-Content "Middleware\GlobalExceptionHandler.cs" -Raw
  $content = $content -replace "Необработанное исключение:", "Unhandled exception:"
  $content = $content -replace "при обработке", "when processing"
  $content = $content -replace "КРИТИЧЕСКАЯ ОШИБКА:", "CRITICAL ERROR:"
  $content = $content -replace "Требуется немедленное вмешательство!", "Immediate attention required!"
  Set-Content "Middleware\GlobalExceptionHandler.cs" $content
  Write-Host "GlobalExceptionHandler.cs - Готово" -ForegroundColor Green
} else {
  Write-Host "GlobalExceptionHandler.cs - Файл не найден" -ForegroundColor Red
}

# FileConverterService.cs замены
Write-Host "Обработка FileConverterService.cs..." -ForegroundColor Cyan
if (Test-Path "Services\FileConverterService.cs") {
  $content = Get-Content "Services\FileConverterService.cs" -Raw
  $content = $content -replace "Начало конвертации файла:", "Starting file conversion:"
  $content = $content -replace "Файл уже конвертирован и найден в кэше:", "File already converted and found in cache:"
  $content = $content -replace "Ошибка при конвертации файла:", "Error converting file:"
  $content = $content -replace "Исходный формат:", "Source format:"
  $content = $content -replace "Конвертация завершена успешно:", "Conversion completed successfully:"
  $content = $content -replace "Время конвертации:", "Conversion time:"
  Set-Content "Services\FileConverterService.cs" $content
  Write-Host "FileConverterService.cs - Готово" -ForegroundColor Green
} else {
  Write-Host "FileConverterService.cs - Файл не найден" -ForegroundColor Red
}

Write-Host "Перевод завершен!" -ForegroundColor Green
```

3. **Сохраните вышеуказанный скрипт в файл** `translate_logs.ps1` в корневой директории проекта

4. **Запустите скрипт**:
   - Откройте PowerShell в директории проекта
   - Выполните: `.\translate_logs.ps1`

## Примечания

- Скрипт изменяет файлы напрямую, рекомендуется сделать резервную копию проекта перед выполнением
- Если строка содержит специальные символы или форматирование, может потребоваться ручная правка
- После выполнения скрипта пересоберите проект 