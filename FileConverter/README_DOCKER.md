# Инструкция по запуску FileConverter в Docker

## Предварительные требования

- Docker
- Docker Compose

## Запуск приложения

1. Сделайте файл init-multiple-databases.sh исполняемым:

```bash
chmod +x init-multiple-databases.sh
```

2. Запустите приложение с помощью Docker Compose:

```bash
docker-compose up -d
```

3. Приложение будет доступно по адресу: http://localhost:8080

## Настройка переменных окружения

Перед запуском вы можете изменить следующие параметры в файле docker-compose.yml:

- `POSTGRES_PASSWORD` - пароль пользователя postgres (по умолчанию: your_password)
- `ConnectionStrings__DefaultConnection` - строка подключения к основной базе данных
- `ConnectionStrings__HangfireConnection` - строка подключения к базе данных Hangfire

## Директории для данных

- `./Logs` - директория для хранения логов
- `./Temp` - директория для временных файлов
- `./C3Storage` - директория для хранения контента

## Остановка приложения

```bash
docker-compose down
```

## Управление контейнерами

- Просмотр логов:

```bash
docker-compose logs -f
```

- Перезапуск приложения:

```bash
docker-compose restart app
```

## Резервное копирование базы данных

```bash
docker exec -t fileconverter_postgres pg_dump -U postgres fileconverter_db > backup_$(date +%Y-%m-%d_%H-%M-%S).sql
``` 