# syntax=docker/dockerfile:1
# Build from the repository root: docker build -f src/WebhookProcessor.Worker/Dockerfile .

# The SDK version matches global.json, so the image builds exactly like CI and local builds.
FROM mcr.microsoft.com/dotnet/sdk:10.0.403-noble AS build
WORKDIR /src

# Project files first: the restore layer is reused until a package reference changes.
COPY global.json ./
COPY src/WebhookProcessor.Core/WebhookProcessor.Core.csproj src/WebhookProcessor.Core/
COPY src/WebhookProcessor.Worker/WebhookProcessor.Worker.csproj src/WebhookProcessor.Worker/
RUN dotnet restore src/WebhookProcessor.Worker/WebhookProcessor.Worker.csproj

COPY src/WebhookProcessor.Core/ src/WebhookProcessor.Core/
COPY src/WebhookProcessor.Worker/ src/WebhookProcessor.Worker/
RUN dotnet publish src/WebhookProcessor.Worker/WebhookProcessor.Worker.csproj \
    --configuration Release --no-restore --output /app /p:UseAppHost=false

# The worker hosts only health endpoints, but those need ASP.NET Core, so it uses the same runtime image.
FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled AS runtime
WORKDIR /app
COPY --from=build /app ./

USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "WebhookProcessor.Worker.dll"]
