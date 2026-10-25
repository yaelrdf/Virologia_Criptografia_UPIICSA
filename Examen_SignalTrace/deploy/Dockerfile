# SignalTrace server image (API + web client + MQTT bridge). Build context: the Examen_SignalTrace folder.
#   docker build -f deploy/Dockerfile -t signaltrace .
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY global.json Directory.Build.props Directory.Packages.props ./
COPY src/SignalTrace.Core/SignalTrace.Core.csproj src/SignalTrace.Core/packages.lock.json src/SignalTrace.Core/
COPY src/SignalTrace.Server/SignalTrace.Server.csproj src/SignalTrace.Server/packages.lock.json src/SignalTrace.Server/
RUN dotnet restore src/SignalTrace.Server/SignalTrace.Server.csproj --locked-mode
COPY src/SignalTrace.Core/ src/SignalTrace.Core/
COPY src/SignalTrace.Server/ src/SignalTrace.Server/
RUN dotnet publish src/SignalTrace.Server/SignalTrace.Server.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
# SQLite DB, JWT secret and the long-term ECDSA key live in /app/data: mount it as a persistent volume,
# otherwise every restart generates a new server key and clients see a fingerprint change.
RUN mkdir -p /app/data && chown -R $APP_UID /app/data
USER $APP_UID
ENV ASPNETCORE_ENVIRONMENT=Production \
    ASPNETCORE_URLS=http://+:8080 \
    Storage__DataDir=/app/data
EXPOSE 8080 1883
VOLUME ["/app/data"]
ENTRYPOINT ["dotnet", "SignalTrace.Server.dll"]
