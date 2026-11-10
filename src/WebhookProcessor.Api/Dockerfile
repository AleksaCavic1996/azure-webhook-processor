# syntax=docker/dockerfile:1
# Build from the repository root: docker build -f src/WebhookProcessor.Api/Dockerfile .

# The SDK version matches global.json, so the image builds exactly like CI and local builds.
FROM mcr.microsoft.com/dotnet/sdk:10.0.403-noble AS build
WORKDIR /src

# Project files first: the restore layer is reused until a package reference changes.
COPY global.json ./
COPY src/WebhookProcessor.Core/WebhookProcessor.Core.csproj src/WebhookProcessor.Core/
COPY src/WebhookProcessor.Api/WebhookProcessor.Api.csproj src/WebhookProcessor.Api/
RUN dotnet restore src/WebhookProcessor.Api/WebhookProcessor.Api.csproj

COPY src/WebhookProcessor.Core/ src/WebhookProcessor.Core/
COPY src/WebhookProcessor.Api/ src/WebhookProcessor.Api/
RUN dotnet publish src/WebhookProcessor.Api/WebhookProcessor.Api.csproj \
    --configuration Release --no-restore --output /app /p:UseAppHost=false

# Chiseled: no shell and no package manager, so there is very little to attack or patch.
# The 10.0 tag picks up runtime security patches on every rebuild.
FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled AS runtime
WORKDIR /app
COPY --from=build /app ./

# The image already defaults to this non-root user. Stating it keeps that visible in review.
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "WebhookProcessor.Api.dll"]
