# --- Build Stage ---
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /app

COPY *.csproj ./
RUN dotnet restore

COPY . ./
RUN dotnet publish -c Release -o out

# --- Runtime Stage ---
FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app

# ✅ Corrige les dépendances bloquées sur Debian
RUN apt-get update && \
    apt-get install -y --no-install-recommends apt-utils && \
    apt-get install -y --fix-missing --no-install-recommends \
        wget gnupg ca-certificates fontconfig libgdiplus xfonts-base xfonts-75dpi tar && \
    rm -rf /var/lib/apt/lists/*

# ✅ Installe wkhtmltopdf (si ton app en a besoin)
RUN wget https://github.com/wkhtmltopdf/packaging/releases/download/0.12.6-1/wkhtmltox_0.12.6-1.bionic_amd64.deb && \
    apt install -y ./wkhtmltox_0.12.6-1.bionic_amd64.deb || true && \
    rm wkhtmltox_0.12.6-1.bionic_amd64.deb

COPY --from=build /app/out .

ENTRYPOINT ["dotnet", "DmsProjeckt.dll"]
