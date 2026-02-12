FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS base
WORKDIR /app

# Instala 7zip (p7zip)
RUN apt-get update \
    && apt-get install -y --no-install-recommends p7zip-full \
    && rm -rf /var/lib/apt/lists/*

# (opcional) garante temp
RUN mkdir -p /tmp/extract-api

EXPOSE 8080

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src
COPY ["unzip-service/unzip-service.csproj", "unzip-service/"]
RUN dotnet restore "unzip-service/unzip-service.csproj"
COPY . .
WORKDIR "/src/unzip-service"
RUN dotnet build "./unzip-service.csproj" -c $BUILD_CONFIGURATION -o /app/build

FROM build AS publish
ARG BUILD_CONFIGURATION=Release
RUN dotnet publish "./unzip-service.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "unzip-service.dll"]