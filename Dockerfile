# Multi-stage build for SbConsole.Web. Build context is the repo root.
#   docker build -t nervecenter/sbconsole:local .
# or via compose: docker compose --profile app up -d --build

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore first, against project files only, so source edits don't bust the package cache.
COPY Directory.Build.props ./
COPY src/SbConsole.Sdk/SbConsole.Sdk.csproj                             src/SbConsole.Sdk/
COPY src/SbConsole.Core/SbConsole.Core.csproj                           src/SbConsole.Core/
COPY src/Plugins/SbConsole.Plugins.ServiceBus/SbConsole.Plugins.ServiceBus.csproj src/Plugins/SbConsole.Plugins.ServiceBus/
COPY src/Plugins/SbConsole.Plugins.Kafka/SbConsole.Plugins.Kafka.csproj           src/Plugins/SbConsole.Plugins.Kafka/
COPY src/Plugins/SbConsole.Plugins.Aws/SbConsole.Plugins.Aws.csproj               src/Plugins/SbConsole.Plugins.Aws/
COPY src/SbConsole.Web/SbConsole.Web.csproj                             src/SbConsole.Web/
RUN dotnet restore src/SbConsole.Web/SbConsole.Web.csproj

COPY src/ src/
RUN dotnet publish src/SbConsole.Web/SbConsole.Web.csproj -c Release --no-restore -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .

# SQLite db + data-protection keys live here (SBC_DB_PATH=/data/sbconsole.db); mount a volume.
RUN mkdir -p /data && chown app:app /data
USER app
VOLUME ["/data"]

ENV SBC_BIND=http://0.0.0.0:8080 \
    SBC_DB_PATH=/data/sbconsole.db
EXPOSE 8080

ENTRYPOINT ["dotnet", "SbConsole.Web.dll"]
