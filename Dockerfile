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

# Installe wkhtmltopdf (libwkhtmltox)
RUN apt-get update && apt-get install -y \
    libgdiplus \
    wkhtmltopdf \
    && rm -rf /var/lib/apt/lists/*

COPY --from=build /app/out .
ENTRYPOINT ["dotnet", "DmsProjeckt.dll"]
