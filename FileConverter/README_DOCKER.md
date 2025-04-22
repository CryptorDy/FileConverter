# Инструкция по запуску FileConverter в Docker

## Предварительные требования

- Docker

## Запуск приложения

1. Соберите Docker образ:

```bash
docker build -t fileconverter .
```

2. Запустите контейнер:

```bash
docker run -d --name fileconverter -p 5080:5080 \
  -e ASPNETCORE_ENVIRONMENT=Production \
  -e ConnectionStrings__DefaultConnection="Server=ваш_сервер;Port=5432;Database=fileconverter_db;User Id=postgres;Password=ваш_пароль;Include Error Detail=true" \
  -e ConnectionStrings__HangfireConnection="Server=ваш_сервер;Port=5432;Database=hangfire_db;User Id=postgres;Password=ваш_пароль;Include Error Detail=true" \
  -v $(pwd)/Logs:/app/Logs \
  -v $(pwd)/Temp:/app/Temp \
  -v $(pwd)/C3Storage:/app/C3Storage \
  fileconverter
```

3. Приложение будет доступно по адресу: http://localhost:5080

## Настройка переменных окружения

Вы можете настроить следующие переменные окружения при запуске контейнера:

- `ConnectionStrings__DefaultConnection` - строка подключения к основной базе данных
- `ConnectionStrings__HangfireConnection` - строка подключения к базе данных Hangfire

## Директории для данных

Вы можете монтировать следующие директории:

- `./Logs:/app/Logs` - директория для хранения логов
- `./Temp:/app/Temp` - директория для временных файлов
- `./C3Storage:/app/C3Storage` - директория для хранения контента

## Управление контейнером

- Остановка контейнера:

```bash
docker stop fileconverter
```

- Удаление контейнера:

```bash
docker rm fileconverter
```

- Просмотр логов:

```bash
docker logs fileconverter
```

- Перезапуск контейнера:

```bash
docker restart fileconverter
```

## Диагностика проблем

Если вы столкнулись с ошибкой 502 Bad Gateway, выполните следующие шаги:

1. Проверьте логи контейнера:
```bash
docker logs fileconverter
```

2. Проверьте доступность приложения внутри контейнера:
```bash
docker exec -it fileconverter curl http://localhost:5080/Health
```

3. Проверьте, запущен ли контейнер:
```bash
docker ps | grep fileconverter
```

4. Перезапустите контейнер:
```bash
docker restart fileconverter
```

5. Проверьте настройки NGINX, если он используется:
   - Убедитесь, что NGINX настроен на проксирование запросов на порт 5080 контейнера
   - Проверьте логи NGINX на наличие ошибок 