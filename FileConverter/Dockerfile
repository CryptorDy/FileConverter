FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS base
WORKDIR /app
EXPOSE 5080
EXPOSE 443

# Стадия сборки Essentia
FROM ghcr.io/mtg/essentia:latest AS essentia-stage
# Этот образ уже содержит скомпилированную Essentia со всеми зависимостями

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY ["FileConverter.csproj", "./"]
COPY ["packages.props", "./"]
RUN dotnet restore "FileConverter.csproj"
COPY . .
WORKDIR "/src"
RUN dotnet build "FileConverter.csproj" -c Release -o /app/build

FROM build AS publish
RUN dotnet publish "FileConverter.csproj" -c Release -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app

# Установка необходимых пакетов и Python зависимостей
RUN apt-get update && \
    apt-get install -y software-properties-common && \
    apt-get install -y \
    python3 \
    python3-pip \
    python3-venv \
    python3-dev \
    ffmpeg \
    curl \
    build-essential \
    pkg-config \
    libfftw3-dev \
    libavcodec-dev \
    libavformat-dev \
    libavutil-dev \
    libswresample-dev \
    libtag1-dev \
    libyaml-cpp-dev && \
    apt-get clean && \
    rm -rf /var/lib/apt/lists/*

# Создаем виртуальное окружение и устанавливаем Essentia
RUN python3 -m venv /opt/venv && \
    /opt/venv/bin/pip install --upgrade pip && \
    /opt/venv/bin/pip install --no-cache-dir numpy scipy pyyaml && \
    /opt/venv/bin/pip install --no-cache-dir essentia || \
    echo "ВНИМАНИЕ: Essentia не установлена, анализ аудио будет пропущен"

# Создаем symlink для удобства
RUN ln -sf /opt/venv/bin/python3 /usr/local/bin/python3-essentia

COPY --from=publish /app/publish .

# Копируем Python скрипт для анализа аудио
COPY Scripts/essentia_analyzer.py /app/Scripts/essentia_analyzer.py
RUN chmod +x /app/Scripts/essentia_analyzer.py

# Создание директорий для временных файлов и логов
RUN mkdir -p /app/Temp /app/Logs /app/wwwroot/mp3

# Создаем директорию для временных файлов и устанавливаем права
RUN mkdir -p /tmp/FileConverter/Temp && chmod -R 777 /tmp/FileConverter

# Установка переменных среды
ENV ASPNETCORE_ENVIRONMENT=Production
ENV ASPNETCORE_URLS=http://+:5080
ENV PYTHONPATH=/usr/local/lib/python3.11/dist-packages:/opt/venv/lib/python3.11/site-packages
ENV PATH="/opt/venv/bin:$PATH"

# Добавление пользователя для работы приложения
RUN adduser --disabled-password --gecos "" appuser
RUN chown -R appuser:appuser /app
USER appuser

# Явно указываем точку входа
ENTRYPOINT ["dotnet", "FileConverter.dll"] 