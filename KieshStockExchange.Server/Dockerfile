# 7d — multi-stage build for the server. Runs as a non-root user on :8080.
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src
COPY ["KieshStockExchange.Server/KieshStockExchange.Server.csproj", "KieshStockExchange.Server/"]
COPY ["KieshStockExchange.Shared/KieshStockExchange.Shared.csproj", "KieshStockExchange.Shared/"]
RUN dotnet restore "KieshStockExchange.Server/KieshStockExchange.Server.csproj"
COPY . .
RUN dotnet publish "KieshStockExchange.Server/KieshStockExchange.Server.csproj" \
    -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS runtime
RUN groupadd -g 1000 appgroup && useradd -m -u 1000 -g appgroup appuser
WORKDIR /app
COPY --from=build --chown=appuser:appgroup /app/publish ./
USER appuser
EXPOSE 8080
HEALTHCHECK --interval=30s --timeout=5s --retries=3 \
    CMD wget -q --spider http://localhost:8080/healthz/ready || exit 1
ENTRYPOINT ["dotnet", "KieshStockExchange.Server.dll"]
