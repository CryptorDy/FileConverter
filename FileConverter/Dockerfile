FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS base
WORKDIR /app
EXPOSE 80
EXPOSE 443

# Установка FFmpeg
RUN apt-get update && \
    apt-get install -y ffmpeg && \
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

# Установка переменных среды
ENV ASPNETCORE_ENVIRONMENT=Production
ENV ASPNETCORE_URLS=http://+:80
ENV FFmpegPath=/usr/bin

# Установка пользователя без привилегий
RUN chown -R 1000:1000 /app
USER 1000

ENTRYPOINT ["dotnet", "FileConverter.dll"] 