# One-time: build a pristine, zero-trade seeded template (kse_soak_seed) for fast per-run resets.
$ErrorActionPreference = "Stop"
$root = "C:\Users\kjden\source\repos\Kieshdh\KieshStockExchange"
$exe  = "$root\KieshStockExchange.Server\bin\Debug\net9.0\KieshStockExchange.Server.exe"
$pg   = "kieshstockexchange-postgres-1"
$db   = "kse_soak"
$tmpl = "kse_soak_seed"
$ts   = Get-Date -Format "yyyyMMdd-HHmmss"
$log  = "$root\logs\balance-setup-$ts.log"
function Stamp { (Get-Date -Format "HH:mm:ss") }

Write-Host "[$(Stamp)] killing any running server"
Get-Process -Name "KieshStockExchange.Server" -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Seconds 2

Write-Host "[$(Stamp)] dropping $db and $tmpl"
docker exec $pg psql -U kse -d postgres -c "SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname IN ('$db','$tmpl') AND pid <> pg_backend_pid();" | Out-Null
docker exec $pg psql -U kse -d postgres -c "DROP DATABASE IF EXISTS $tmpl;" | Out-Null
docker exec $pg psql -U kse -d postgres -c "DROP DATABASE IF EXISTS $db;" | Out-Null
docker exec $pg psql -U kse -d postgres -c "CREATE DATABASE $db;" | Out-Null

$env:KSE_DB_CONNECTION_STRING = "Host=localhost;Port=5432;Database=$db;Username=kse;Password=kse-dev"
Write-Host "[$(Stamp)] applying EF migrations to $db"
dotnet ef database update --project "$root\KieshStockExchange.Server\KieshStockExchange.Server.csproj" 2>&1 | Select-Object -Last 3
if ($LASTEXITCODE -ne 0) { throw "ef database update failed (exit $LASTEXITCODE)" }

Write-Host "[$(Stamp)] launching server BOTS-OFF to seed $db -> $log"
$env:ASPNETCORE_ENVIRONMENT   = "Development"
$env:Bots__AutoStart          = "false"
$env:Seed__AutoOnEmptyDb      = "true"
$proc = Start-Process -FilePath $exe -PassThru -WindowStyle Hidden `
        -WorkingDirectory "$root\KieshStockExchange.Server" `
        -RedirectStandardOutput $log -RedirectStandardError "$log.err"

try {
  $ready = $false
  for ($i = 0; $i -lt 200; $i++) {
    Start-Sleep -Seconds 3
    if ($proc.HasExited) { throw "server exited during seed (exit $($proc.ExitCode)); see $log.err" }
    if (Select-String -Path $log -Pattern "Application started" -Quiet -ErrorAction SilentlyContinue) { $ready = $true; break }
  }
  if (-not $ready) { throw "server never reached 'Application started' within timeout" }
  Write-Host "[$(Stamp)] seed complete; settling 5s then stopping"
  Start-Sleep -Seconds 5
}
finally {
  if (-not $proc.HasExited) { Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue }
  $env:Bots__AutoStart     = $null
  $env:Seed__AutoOnEmptyDb = $null
}
Start-Sleep -Seconds 2

Write-Host "[$(Stamp)] cloning $db -> $tmpl (pristine template)"
docker exec $pg psql -U kse -d postgres -c "SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname = '$db' AND pid <> pg_backend_pid();" | Out-Null
docker exec $pg psql -U kse -d postgres -c "CREATE DATABASE $tmpl TEMPLATE $db;" | Out-Null

$orders = docker exec $pg psql -U kse -d $tmpl -tAc "SELECT count(*) FROM ""Orders"";"
$tx     = docker exec $pg psql -U kse -d $tmpl -tAc "SELECT count(*) FROM ""Transactions"";"
$prices = docker exec $pg psql -U kse -d $tmpl -tAc "SELECT count(*) FROM ""StockPrices"";"
Write-Host "[$(Stamp)] template ready: orders=$orders tx=$tx seedPrices=$prices (orders/tx should be 0)"
