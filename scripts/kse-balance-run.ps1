param(
  [Parameter(Mandatory=$true)][string]$Label,   # short config label, e.g. "E1"
  [int]$Minutes = 6,                             # trading minutes after warm-up
  [int]$SampleEverySec = 120,                    # drift sampling cadence
  [string]$Note = ""                             # free-text config note for the results row
)
$ErrorActionPreference = "Stop"
$root  = "C:\Users\kjden\source\repos\Kieshdh\KieshStockExchange"
$exe   = "$root\KieshStockExchange.Server\bin\Debug\net9.0\KieshStockExchange.Server.exe"
$tmpl  = "kse_soak_seed"
$db    = "kse_soak"
$conn  = "Host=localhost;Port=5432;Database=$db;Username=kse;Password=kse-dev"
$pg    = "kieshstockexchange-postgres-1"
$sql   = "$root\scripts\balance-drift.sql"
$depthSql = "$root\scripts\balance-depth.sql"
$ts    = Get-Date -Format "yyyyMMdd-HHmmss"
$log   = "$root\logs\balance-$Label-$ts.log"
$res   = "$root\logs\balance-results.csv"

function Stamp { (Get-Date -Format "HH:mm:ss") }
function Drift {
  $out = Get-Content $sql -Raw | docker exec -i $pg psql -U kse -d $db -tA 2>$null
  return ($out | Where-Object { $_ -match ',' } | Select-Object -First 1).Trim()
}
function Depth {
  $out = Get-Content $depthSql -Raw | docker exec -i $pg psql -U kse -d $db -tA 2>$null
  return ($out | Where-Object { $_ -match ',' } | Select-Object -First 1).Trim()
}

Write-Host "[$(Stamp)] $Label : resetting $db from template $tmpl"
docker exec $pg psql -U kse -d postgres -c "SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname IN ('$db','$tmpl') AND pid <> pg_backend_pid();" | Out-Null
docker exec $pg psql -U kse -d postgres -c "DROP DATABASE IF EXISTS $db;" | Out-Null
docker exec $pg psql -U kse -d postgres -c "CREATE DATABASE $db TEMPLATE $tmpl;" | Out-Null

Write-Host "[$(Stamp)] $Label : launching server (bots on) -> $log"
$env:KSE_DB_CONNECTION_STRING = $conn
$env:ASPNETCORE_ENVIRONMENT   = "Development"
$proc = Start-Process -FilePath $exe -PassThru -WindowStyle Hidden `
        -WorkingDirectory "$root\KieshStockExchange.Server" `
        -RedirectStandardOutput $log -RedirectStandardError "$log.err"

try {
  # Wait for the bot loop to start (warm-up done). Cap ~6 min.
  $ready = $false
  for ($i = 0; $i -lt 120; $i++) {
    Start-Sleep -Seconds 3
    if ($proc.HasExited) { throw "server exited during warm-up (exit $($proc.ExitCode)); see $log.err" }
    if (Select-String -Path $log -Pattern "starting bot loop" -Quiet -ErrorAction SilentlyContinue) { $ready = $true; break }
  }
  if (-not $ready) { throw "server never reached 'starting bot loop' within timeout" }
  Write-Host "[$(Stamp)] $Label : READY, trading for $Minutes min"

  $start = Get-Date
  $deadline = $start.AddMinutes($Minutes)
  $last = ""
  while ((Get-Date) -lt $deadline) {
    Start-Sleep -Seconds $SampleEverySec
    $last = Drift
    $depth = Depth
    $elapsed = [int]((Get-Date) - $start).TotalMinutes
    Write-Host "[$(Stamp)] $Label t=${elapsed}m  drift(stocks,avg,stddev,min,max,beyond50,trades)=$last  depth(open,restQty,<1%,1-5%,5-20%,>20%)=$depth"
  }

  $final = Drift
  Write-Host "[$(Stamp)] $Label : DONE final = $final"
  if (-not (Test-Path $res)) {
    "timestamp,label,minutes,stocks,avg_pct,stddev_pct,min_pct,max_pct,beyond50,trades,note" | Out-File -FilePath $res -Encoding utf8
  }
  "$ts,$Label,$Minutes,$final,$Note" | Out-File -FilePath $res -Append -Encoding utf8
}
finally {
  if (-not $proc.HasExited) {
    Write-Host "[$(Stamp)] $Label : stopping server (pid $($proc.Id))"
    Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
  }
}
