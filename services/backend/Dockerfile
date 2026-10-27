FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

COPY services/backend/src ./src

RUN dotnet publish src/HTBAM.Api/HTBAM.Api.csproj \
    -c Release \
    -o /out

FROM mcr.microsoft.com/dotnet/aspnet:9.0
WORKDIR /app

RUN apt-get update \
    && apt-get install -y --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/*

COPY --from=build /out .
COPY database/sqlserver /app/migrations

EXPOSE 8080

ENTRYPOINT ["dotnet", "HTBAM.Api.dll"]
