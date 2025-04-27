FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS base
WORKDIR /app
EXPOSE 5080
EXPOSE 443

# Установка FFmpeg
RUN apt-get update && \
    apt-get install -y ffmpeg curl && \
    apt-get clean && \
    rm -rf /var/lib/apt/lists/*

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
COPY --from=publish /app/publish .

# Создание директорий для временных файлов и логов
RUN mkdir -p /app/Temp /app/Logs /app/wwwroot/mp3

# Создаем директорию для временных файлов и устанавливаем права
RUN mkdir -p /tmp/FileConverter/Temp && chmod -R 777 /tmp/FileConverter

# Установка переменных среды
ENV ASPNETCORE_ENVIRONMENT=Production
ENV ASPNETCORE_URLS=http://+:5080

# Добавление пользователя для работы приложения
RUN adduser --disabled-password --gecos "" appuser
RUN chown -R appuser:appuser /app
USER appuser

# Явно указываем точку входа
ENTRYPOINT ["dotnet", "FileConverter.dll"] 