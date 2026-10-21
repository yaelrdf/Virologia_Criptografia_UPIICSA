# Imagen del servidor PRISMA-L (API + web). Contexto de construcción: la carpeta Examen_Prisma.
#   docker build -f deploy/Dockerfile -t prisma-l .
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY src/Prisma.Core/Prisma.Core.csproj src/Prisma.Core/
COPY src/Prisma.Api/Prisma.Api.csproj src/Prisma.Api/
RUN dotnet restore src/Prisma.Api/Prisma.Api.csproj
COPY src/ src/
RUN dotnet publish src/Prisma.Api/Prisma.Api.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
# La base de datos SQLite y el catálogo generado viven en /app/data (móntalo como volumen persistente).
RUN mkdir -p /app/data && chown -R $APP_UID /app/data
USER $APP_UID
ENV ASPNETCORE_ENVIRONMENT=Production \
    ASPNETCORE_URLS=http://+:8080 \
    ConnectionStrings__Default="Data Source=/app/data/prisma.db" \
    Catalog__Directory=/app/data/catalog
EXPOSE 8080
VOLUME ["/app/data"]
ENTRYPOINT ["dotnet", "Prisma.Api.dll"]
