#!/bin/bash

echo "=== FileConverter Docker Build ==="
echo "Сборка с поддержкой Essentia..."

# Сборка с основным Dockerfile и fallback на виртуальное окружение
docker build -t fileconverter:latest . || {
    echo "Основная сборка не удалась, пробуем альтернативный Dockerfile..."
    docker build -f Dockerfile.alternative -t fileconverter:latest .
}

echo "Сборка завершена!"
echo "Для запуска используйте:"
echo "docker run -p 5080:5080 fileconverter:latest" 