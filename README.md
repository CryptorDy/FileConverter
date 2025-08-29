# FileConverter - Сервис конвертации видео в MP3

Система для безопасной и масштабируемой конвертации видео в MP3 формат.

## Основные возможности

- ✅ Конвертация видео из различных источников в MP3
- ✅ Пакетная обработка нескольких видео одновременно
- ✅ Кэширование результатов для оптимизации повторных запросов
- ✅ Распределенная обработка задач с использованием Hangfire
- ✅ Управление временными файлами с автоматической очисткой
- ✅ Мониторинг и сбор метрик производительности
- ✅ Безопасная валидация URL без ограничения доменов
- ✅ Хранение результатов в объектном хранилище (S3-совместимое)

## Технологии

- ASP.NET Core 8.0
- Entity Framework Core для работы с базой данных
- PostgreSQL для хранения данных
- Hangfire для фоновых задач
- In-Memory кэширование для оптимизации производительности
- Serilog для структурированного логирования
- Swagger для документации API

## Необходимые зависимости

- .NET 8.0 SDK или выше
- PostgreSQL 12.0 или выше
- FFmpeg (для конвертации видео)

## Установка зависимостей

### Установка PostgreSQL

#### Windows:
1. Скачайте PostgreSQL с официального сайта: https://www.postgresql.org/download/windows/
2. Запустите установщик и следуйте инструкциям
3. Запомните пароль для пользователя postgres, он понадобится для конфигурации

#### Linux (Ubuntu/Debian):
```bash
sudo apt update
sudo apt install postgresql postgresql-contrib
```

### Установка FFmpeg

#### Windows:
1. Скачайте FFmpeg с официального сайта: https://ffmpeg.org/download.html
2. Распакуйте архив
3. Добавьте путь к папке bin в переменную среды PATH

#### Linux (Ubuntu/Debian):
```bash
sudo apt update
sudo apt install ffmpeg
```

## Настройка проекта

1. Клонируйте репозиторий:
```bash
git clone https://github.com/yourusername/FileConverter.git
cd FileConverter
```

2. Восстановите пакеты NuGet:
```bash
dotnet restore
```

3. Установите дополнительные пакеты:
```bash
# Для каждой строки в файле packages.props выполните:
dotnet add package [имя_пакета]
```

4. Обновите строки подключения в `appsettings.json`:
   - `DefaultConnection` - для основной БД
   - `HangfireConnection` - для БД Hangfire

   Пример строки подключения:
   ```
   "Host=localhost;Port=5432;Database=fileconverter;Username=postgres;Password=yourpassword"
   ```

5. Примените миграции базы данных:
```bash
dotnet ef database update
```

## Запуск приложения

### Режим разработки:
```bash
dotnet run
```

### Продакшн:
```bash
dotnet publish -c Release
cd bin/Release/net8.0/publish
dotnet FileConverter.dll
```

## Масштабирование и производительность

Система спроектирована для эффективной работы:

- **Оптимизированное кэширование в памяти** с ограничением размера
- **Ограничение одновременных операций** для контроля нагрузки
- **Очереди приоритетов** для важных задач
- **Балансировка нагрузки** с помощью Hangfire
- **Мониторинг производительности** с автоматическими метриками

### Очереди и конвейер обработки

Система использует каналы (очереди) для построения конвейера обработки. Все каналы теперь неограниченные (unbounded) — задания не отбрасываются из‑за переполнения. Нагрузка контролируется параметрами максимального параллелизма `Performance:MaxConcurrent*`.

- DownloadChannel: входная очередь загрузок
  - Сообщение: `(JobId, VideoUrl)`
  - Источник: контроллер/Recovery сервис
  - Потребитель: DownloadBackgroundService

- ConversionChannel: очередь конвертации видео в аудио
  - Сообщение: `(JobId, VideoPath, VideoHash)`
  - Источник: DownloadBackgroundService
  - Потребитель: ConversionBackgroundService

- AudioAnalysisChannel: очередь анализа аудио (Essentia)
  - Сообщение: `(JobId, Mp3Path, VideoPath, VideoHash)`
  - Источник: ConversionBackgroundService
  - Потребитель: AudioAnalysisBackgroundService

- KeyframeExtractionChannel: очередь извлечения ключевых кадров
  - Сообщение: `(JobId, VideoPath, Mp3Path, VideoHash)`
  - Источник: AudioAnalysisBackgroundService
  - Потребитель: KeyframeExtractionBackgroundService

- UploadChannel: очередь загрузки результатов в хранилище
  - Сообщение: `(JobId, Mp3Path, VideoPath, VideoHash, KeyframeInfos)`
  - Источник: KeyframeExtractionBackgroundService
  - Потребитель: UploadBackgroundService

- YoutubeDownloadChannel: очередь загрузок с YouTube
  - Сообщение: `(JobId, VideoUrl)`
  - Источник: контроллер/Recovery сервис
  - Потребитель: YoutubeBackgroundService

Важно: метрики и параллелизм настраиваются через `Performance:MaxConcurrentDownloads`, `MaxConcurrentConversions`, `MaxConcurrentAudioAnalyses`, `MaxConcurrentKeyframeExtractions`, `MaxConcurrentUploads`, `MaxConcurrentYoutubeDownloads`. Ключи емкостей очередей удалены и не используются.

## Мониторинг и администрирование

- **Панель Hangfire**: `/hangfire` (только в режиме разработки)
- **Метрики**: `/api/metrics/summary`
- **Статус системы**: `/health`
- **Очистка временных файлов**: `/api/metrics/cleanup-temp`

## Конфигурация

Основные настройки находятся в `appsettings.json`:

- **FileConverter** - настройки конвертации и обработки файлов
- **Caching** - настройки кэширования
- **Metrics** - настройки сбора метрик
- **Performance** - настройки производительности
- **HealthChecks** - настройки проверок состояния

## Безопасность

- Базовая валидация URL (проверка синтаксиса, блокировка локальных адресов и IP)
- Проверка размера файлов перед загрузкой
- Безопасное управление временными файлами
- Ограничение скорости запросов (rate limiting)
- Глобальная обработка ошибок без раскрытия технических деталей

## API

### 1. VideoConverterController

#### Запрос на конвертацию (начало задачи)
```
POST /api/videoconverter/to-mp3
```

**Тело запроса:**
```json
{
  "videoUrls": [
    "https://example.com/video1.mp4",
    "https://example.com/video2.mp4"
  ]
}
```

**Ответ:**
```json
{
  "batchId": "6f8d9ac7-3bce-4e7f-a5d1-2f3c7d9e8b0a",
  "jobs": [
    {
      "jobId": "5f3a1d68-92a7-4f0f-b520-d51e3d3c8b2d",
      "statusUrl": "https://yourserver.com/api/videoconverter/status/5f3a1d68-92a7-4f0f-b520-d51e3d3c8b2d"
    },
    {
      "jobId": "7e5b2c89-4d13-6f12-c630-e42f4d5c9b3e",
      "statusUrl": "https://yourserver.com/api/videoconverter/status/7e5b2c89-4d13-6f12-c630-e42f4d5c9b3e"
    }
  ],
  "batchStatusUrl": "https://yourserver.com/api/videoconverter/batch-status/6f8d9ac7-3bce-4e7f-a5d1-2f3c7d9e8b0a"
}
```

#### Получение статуса одной задачи
```
GET /api/videoconverter/status/{jobId}
```

**Ответ:**
```json
{
  "jobId": "5f3a1d68-92a7-4f0f-b520-d51e3d3c8b2d",
  "status": "Converting",
  "mp3Url": null,
  "errorMessage": null,
  "progress": 65.0
}
```

#### Получение статуса пакета задач
```
GET /api/videoconverter/batch-status/{batchId}
```

**Ответ:**
```json
[
  {
    "jobId": "5f3a1d68-92a7-4f0f-b520-d51e3d3c8b2d",
    "status": "Completed",
    "mp3Url": "https://storage.example.com/mp3/5f3a1d68-92a7-4f0f-b520-d51e3d3c8b2d.mp3",
    "errorMessage": null,
    "progress": 100.0
  },
  {
    "jobId": "7e5b2c89-4d13-6f12-c630-e42f4d5c9b3e",
    "status": "Converting",
    "mp3Url": null,
    "errorMessage": null,
    "progress": 30.0
  }
]
```

**Возможные статусы:**
- `Pending` (ожидает обработки)
- `Downloading` (загрузка видео)
- `Converting` (процесс конвертации)
- `Uploading` (загрузка MP3 в хранилище)
- `Completed` (завершено - в этом случае будет доступен mp3Url)
- `Failed` (ошибка - в этом случае будет errorMessage)

#### Получение списка всех задач
```
GET /api/videoconverter/jobs?skip=0&take=20
```

**Параметры:**
- `skip` (опционально, по умолчанию 0) - сколько задач пропустить 
- `take` (опционально, по умолчанию 20) - сколько задач вернуть

**Ответ:**
```json
[
  {
    "jobId": "5f3a1d68-92a7-4f0f-b520-d51e3d3c8b2d",
    "status": "Completed",
    "mp3Url": "https://storage.example.com/mp3/5f3a1d68-92a7-4f0f-b520-d51e3d3c8b2d.mp3",
    "errorMessage": null,
    "progress": 100.0
  },
  // другие задачи...
]
```

### 2. MetricsController

#### Получение сводки метрик
```
GET /api/metrics/summary
```

**Ответ:** Детальная информация о производительности системы:
- Системная информация (загрузка CPU, память)
- Статистика запросов (общее количество, успешные, ошибки)
- Статистика конвертаций (общее количество, успешные, неудачные)
- Информация о временных файлах
- Метрики производительности

#### Проверка здоровья системы
```
GET /api/metrics/health
```

**Ответ:**
```json
{
  "status": "Healthy",
  "timestamp": "2024-01-15T14:30:22Z",
  "version": "1.0.0",
  "issues": []
}
```

#### Очистка временных файлов
```
POST /api/metrics/cleanup-temp?hoursOld=24
```

**Параметры:**
- `hoursOld` (опционально, по умолчанию 24) - удалить файлы старше указанного количества часов

**Ответ:**
```json
{
  "success": true,
  "hoursOld": 24,
  "filesRemoved": 15,
  "spaceFreedMb": 450
}
```

### Получение MP3 файла

После завершения конвертации, в ответе `JobStatusResponse` будет указано поле `mp3Url`, которое содержит прямую ссылку на скачивание MP3-файла.

Эта ссылка действительна в течение 1 часа с момента создания, после чего файл автоматически удаляется системой (через Mp3CleanupJob).

### Особенности работы

1. Система работает с пакетами задач (batch jobs) - можно отправить несколько URL видео в одном запросе
2. Для каждого URL создается отдельная задача конвертации
3. Можно отслеживать как отдельные задачи, так и весь пакет целиком
4. Статус задачи включает прогресс конвертации в процентах (поле progress)
5. В случае успешной конвертации в поле mp3Url будет ссылка на скачивание
6. В случае ошибки поле errorMessage будет содержать информацию о проблеме

## Хранение файлов

Сконвертированные MP3 файлы хранятся в директории `wwwroot/mp3` и доступны по относительному пути `/mp3/{filename}`.

## Примечания

- Временные файлы автоматически удаляются после конвертации
- Для больших видео может потребоваться увеличение таймаута сервера 