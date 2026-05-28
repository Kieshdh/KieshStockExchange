# Cleanly shuts down the KieshStockExchange server.
# Calls IHostApplicationLifetime.StopApplication() via HTTP so every
# IHostedService.StopAsync runs (bot loop drains, ringbuffer files flush)
# before the process exits.
#
# Default base URL is http://localhost:5000 - override via -BaseUrl when
# running against a non-default port.

param(
    [string]$BaseUrl = "http://localhost:5000"
)

$ErrorActionPreference = "Stop"

try {
    $result = Invoke-RestMethod -Uri "$BaseUrl/api/server/shutdown" -Method Post -TimeoutSec 5
    Write-Output "Shutdown request accepted: $($result.status)"
}
catch {
    Write-Warning "Could not reach $BaseUrl/api/server/shutdown - server may already be stopped."
    Write-Warning $_.Exception.Message
    exit 1
}
