# syntax=docker/dockerfile:1.6
# Multi-stage build for SideQuest .NET 9 backend.
# Designed to run on Railway, Render, Fly.io, Azure App Service, etc.

# ─── Build stage ─────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

# Restore as a separate layer for better caching
COPY ["sidequest.backend.csproj", "./"]
RUN dotnet restore "sidequest.backend.csproj"

# Copy the rest of the source and publish
COPY . .
RUN dotnet publish "sidequest.backend.csproj" \
    -c Release \
    -o /app/publish \
    /p:UseAppHost=false \
    /p:GenerateDocumentationFile=false

# ─── Runtime stage ───────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS runtime
WORKDIR /app

# Copy published artifacts
COPY --from=build /app/publish .

# Ensure uploads directory exists (matches Program.cs expectation)
RUN mkdir -p /app/wwwroot/uploads

# Run as non-root for security
RUN useradd -m -u 1001 appuser && chown -R appuser:appuser /app
USER appuser

# Default port; Railway/Render/Fly override via ASPNETCORE_URLS or PORT env
ENV ASPNETCORE_URLS=http://+:8080
ENV ASPNETCORE_ENVIRONMENT=Production
EXPOSE 8080

ENTRYPOINT ["dotnet", "sidequest.backend.dll"]
