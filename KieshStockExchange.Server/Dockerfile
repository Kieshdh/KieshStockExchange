# 7d — multi-stage build for the server. Runs as a non-root user on :8080.
# (Sweep) Healthcheck fixed: curl + a start-period long enough for the candle-backfill
# boot (~60-90s), so a slow start isn't mistaken for an unhealthy container.
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
# curl for the HEALTHCHECK below — the aspnet image ships neither wget nor curl, so the
# old wget-based check failed 100% of the time ("wget: not found"), leaving the container
# permanently unhealthy and stuck in an autoheal restart loop.
RUN apt-get update && apt-get install -y --no-install-recommends curl && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build --chown=appuser:appgroup /app/publish ./
# WORKDIR /app was created as root; pre-create + chown the dirs the app writes to
# (ReservationLedger -> /app/data, serilog -> /app/logs volume mount point) so the
# non-root runtime user can create files there.
RUN mkdir -p /app/data /app/logs && chown -R appuser:appgroup /app
USER appuser
EXPOSE 8080
HEALTHCHECK --interval=30s --timeout=10s --start-period=120s --retries=5 \
    CMD curl -fsS http://localhost:8080/healthz/ready || exit 1
ENTRYPOINT ["dotnet", "KieshStockExchange.Server.dll"]
