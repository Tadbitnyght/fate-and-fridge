# syntax=docker/dockerfile:1
#
# Fate & Fridge — single application image.
#
# Stages:
#   frontend-build  builds the React/Vite frontend
#   backend-build   restores and publishes the ASP.NET Core backend
#   runtime         serves the API and the built frontend
#
# Migrations are applied by the application at startup when
# Database__MigrateOnStartup=true (set by compose.yaml).

# --------------------------------------------------------------- frontend ---
FROM node:24-alpine AS frontend-build
WORKDIR /build
COPY frontend/package.json frontend/package-lock.json ./
RUN npm ci --no-audit --no-fund
COPY frontend/ ./
RUN npm run build

# ----------------------------------------------------------- backend build ---
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS backend-build
ENV DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    DOTNET_NOLOGO=1
WORKDIR /src
COPY backend/Directory.Build.props backend/global.json ./
COPY backend/src/FateAndFridge.Api/FateAndFridge.Api.csproj src/FateAndFridge.Api/
RUN dotnet restore src/FateAndFridge.Api/FateAndFridge.Api.csproj
COPY backend/src/FateAndFridge.Api/ src/FateAndFridge.Api/
RUN dotnet publish src/FateAndFridge.Api/FateAndFridge.Api.csproj \
    --configuration Release \
    --output /app/publish \
    --no-restore

# ---------------------------------------------------------------- runtime ---
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
RUN apt-get update \
    && apt-get install -y --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /app
COPY --from=backend-build /app/publish ./
COPY --from=frontend-build /build/dist ./wwwroot/

ENV ASPNETCORE_URLS=http://+:8080 \
    ASPNETCORE_ENVIRONMENT=Production \
    DataDir=/data

EXPOSE 8080

HEALTHCHECK --interval=30s --timeout=10s --start-period=30s --retries=3 \
    CMD curl -fsS http://127.0.0.1:8080/api/v1/health || exit 1

ENTRYPOINT ["dotnet", "FateAndFridge.Api.dll"]
