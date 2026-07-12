# RE-ANCHOR RESEED driver (local validation / prod steps 3-5). Full sequence:
#   0. BACKUP the DB (pg_dump) - caller's job on prod.
#   1. STOP the server (the SQL must run on a quiet DB).
#   2. Apply scripts/reanchor.sql  (keeps Stocks/Listings/Candles, anchors SeedPrice+StockPrices to
#      the last candle close, truncates the population).
#   3. START the server with Bots__AutoStart=false (auto-seed SKIPS - Stocks populated; the OLD
#      population must NOT trade at the new anchors while it awaits replacement).
#   4. POST the workbook to the admin subset-seed endpoints: users -> ai-profiles -> holdings
#      (these reset+insert population tables only; they never touch stocks/listings/candles).
#   5. RESTART the server normally (AutoStart on) so the bot loop loads the NEW population and the
#      market opens AT the anchor.
# This script performs steps 2 and 4 (the parts that are identical local/prod). Steps 1/3/5 differ
# per environment (local exe vs docker compose) and stay with the operator.
param(
  [Parameter(Mandatory=$true)][string]$DbName,
  [string]$Container = "kieshstockexchange-postgres-1",
  [string]$BaseUrl = "http://localhost:5081",
  [string]$Xlsx = "$PSScriptRoot\..\KieshStockExchange.Server\Resources\Raw\AIUserData.xlsx",
  [string]$AdminUser = "admin",
  [string]$AdminPass = "hallo123",
  [ValidateSet("sql","seed","all")][string]$Step = "all"
)
$ErrorActionPreference = "Stop"

if ($Step -in @("sql","all")) {
  Write-Host "[reanchor] applying reanchor.sql to $DbName (server must be STOPPED)"
  Get-Content "$PSScriptRoot\reanchor.sql" -Raw | docker exec -i $Container psql -U kse -d $DbName -v ON_ERROR_STOP=1
  if ($LASTEXITCODE -ne 0) { throw "reanchor.sql failed (exit $LASTEXITCODE)" }
  Write-Host "[reanchor] SQL done - listings anchored, population truncated. START the server, then run -Step seed."
}

if ($Step -in @("seed","all")) {
  Write-Host "[reanchor] waiting for the server at $BaseUrl"
  $deadline = (Get-Date).AddMinutes(3)
  while ($true) {
    try { Invoke-WebRequest -Uri "$BaseUrl/healthz" -UseBasicParsing -TimeoutSec 3 | Out-Null; break }
    catch { if ($_.Exception.Response) { break } }  # 401 = serving
    if ((Get-Date) -gt $deadline) { throw "server not reachable at $BaseUrl" }
    Start-Sleep -Seconds 3
  }

  Write-Host "[reanchor] admin login"
  $login = Invoke-RestMethod -Method Post -Uri "$BaseUrl/api/auth/login" -ContentType "application/json" `
           -Body (@{ username = $AdminUser; password = $AdminPass } | ConvertTo-Json)
  $token = $login.token
  if (-not $token) { throw "no token from login" }

  foreach ($kind in @("users", "ai-profiles", "holdings")) {
    Write-Host "[reanchor] seeding $kind from $Xlsx"
    # curl.exe multipart upload (Invoke-RestMethod -Form is PS6+; this must run on 5.1)
    $resp = & curl.exe -s -w "`n%{http_code}" -X POST -H "Authorization: Bearer $token" `
              -F "file=@$Xlsx" "$BaseUrl/api/admin/seed/excel/$kind"
    $code = ($resp | Select-Object -Last 1)
    if ($code -ne "200") { throw "seed $kind failed (HTTP $code): $($resp | Select-Object -First 1)" }
  }
  Write-Host "[reanchor] population seeded. RESTART the server so the bot loop loads the new population."
}
