# --- Build Stage ---
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

ENV DOTNET_CLI_HOME=/tmp
ENV NUGET_PACKAGES=/tmp/nuget/packages
ENV NUGET_HTTP_CACHE_PATH=/tmp/nuget/http-cache
ENV DOTNET_NOLOGO=true
ENV DOTNET_CLI_TELEMETRY_OPTOUT=1

# Saubere NuGet.config (nur nuget.org)
RUN printf '%s\n' \
'<?xml version="1.0" encoding="utf-8"?>' \
'<configuration>' \
'  <packageSources>' \
'    <clear />' \
'    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />' \
'  </packageSources>' \
'</configuration>' \
> NuGet.config

COPY *.csproj ./
RUN dotnet restore ./DmsProjeckt.csproj --configfile ./NuGet.config --disable-parallel

COPY . ./
RUN dotnet publish ./DmsProjeckt.csproj -c Release -o /app/out \
    --no-restore --configfile ./NuGet.config --disable-parallel -p:RestoreFallbackFolders=

# --- Runtime Stage ---
FROM mcr.microsoft.com/dotnet/aspnet:8.0-bookworm-slim AS runtime
WORKDIR /app

ENV ASPNETCORE_URLS=http://+:8080
ENV DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=false
ENV LD_LIBRARY_PATH=/app/Native

# 1) Security: OS Updates mitnehmen
# 2) wkhtmltox/DinkToPdf deps installieren
# 3) Optional: tar entfernen (wenn du es nicht brauchst)
RUN apt-get update \
 && apt-get -y upgrade \
 && apt-get install -y --no-install-recommends \
    ca-certificates \
    libfontconfig1 \
    libfreetype6 \
    libx11-6 \
    libxext6 \
    libxrender1 \
    libjpeg62-turbo \
    libpng16-16 \
 && apt-get purge -y --auto-remove tar || true \
 && rm -rf /var/lib/apt/lists/*

# Non-root user (Best Practice)
RUN useradd -m -u 10001 appuser \
 && chown -R appuser:appuser /app
USER appuser

EXPOSE 8080

COPY --from=build /app/out ./

# Optional Healthcheck (wenn du willst):
# HEALTHCHECK --interval=30s --timeout=3s --start-period=20s --retries=3 \
#   CMD wget -qO- http://127.0.0.1:8080/health || exit 1

ENTRYPOINT ["dotnet", "DmsProjeckt.dll"]
