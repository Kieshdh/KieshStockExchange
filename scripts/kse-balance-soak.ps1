# Long overnight soak: drift + depth + CONSERVATION monitoring (log-scan). Resets kse_soak from the
# template, launches the built server against it, samples every interval, kills the server at the end.
param(
  [int]$Minutes = 240,
  [int]$SampleEverySec = 600,
  [string]$Note = "4h overnight soak (current balancing base)"
)
$ErrorActionPreference = "Stop"
$root = "C:\Users\kjden\source\repos\Kieshdh\KieshStockExchange"
$exe  = "$root\KieshStockExchange.Server\bin\Debug\net9.0\KieshStockExchange.Server.exe"
$tmpl = "kse_soak_seed"; $db = "kse_soak"; $pg = "kieshstockexchange-postgres-1"
$conn = "Host=localhost;Port=5432;Database=$db;Username=kse;Password=kse-dev"
$driftSql = "$root\scripts\balance-drift.sql"
$depthSql = "$root\scripts\balance-depth.sql"
$ts  = Get-Date -Format "yyyyMMdd-HHmmss"
$log = "$root\logs\soak4h-$ts.log"
$res = "$root\logs\soak4h-results-$ts.csv"
function Stamp { (Get-Date -Format "HH:mm:ss") }
function Q($f) { (Get-Content $f -Raw | docker exec -i $pg psql -U kse -d $db -tA 2>$null | Where-Object { $_ -match ',' } | Select-Object -First 1).Trim() }
function Cnt($pat) { (Select-String -Path $log -Pattern $pat -ErrorAction SilentlyContinue | Measure-Object).Count }

Write-Host "[$(Stamp)] SOAK4H resetting $db from $tmpl"
docker exec $pg psql -U kse -d postgres -c "SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname IN ('$db','$tmpl') AND pid <> pg_backend_pid();" | Out-Null
docker exec $pg psql -U kse -d postgres -c "DROP DATABASE IF EXISTS $db;" | Out-Null
docker exec $pg psql -U kse -d postgres -c "CREATE DATABASE $db TEMPLATE $tmpl;" | Out-Null

Write-Host "[$(Stamp)] SOAK4H launching server -> $log"
$env:KSE_DB_CONNECTION_STRING = $conn
$env:ASPNETCORE_ENVIRONMENT   = "Development"
$proc = Start-Process -FilePath $exe -PassThru -WindowStyle Hidden `
        -WorkingDirectory "$root\KieshStockExchange.Server" `
        -RedirectStandardOutput $log -RedirectStandardError "$log.err"
"timestamp,elapsedMin,drift(stocks;avg;stddev;medAbs;min;max;b50;b100;trades),depth(open;rest;<1;1-5;5-20;>20),ERR,CK,CONS,shortfall,lastReconcile" | Out-File $res -Encoding utf8
try {
  $ready = $false
  for ($i = 0; $i -lt 160; $i++) {
    Start-Sleep -Seconds 3
    if ($proc.HasExited) { throw "server exited during warm-up (see $log.err)" }
    if (Select-String -Path $log -Pattern "starting bot loop" -Quiet -ErrorAction SilentlyContinue) { $ready = $true; break }
  }
  if (-not $ready) { throw "server never reached bot loop" }
  Write-Host "[$(Stamp)] SOAK4H READY — soaking $Minutes min, sampling every $([int]($SampleEverySec/60))m"

  $start = Get-Date; $deadline = $start.AddMinutes($Minutes)
  while ((Get-Date) -lt $deadline) {
    Start-Sleep -Seconds $SampleEverySec
    $drift = Q $driftSql; $depth = Q $depthSql
    $err   = Cnt "\[ERR\]"
    $ck    = Cnt "check constraint|CK_Positions|CK_Funds"
    $cons  = Cnt "Conservation"
    $short = Cnt "Short-close collateral shortfall"
    $rec   = (Select-String -Path $log -Pattern "Reservation reconcile:" -ErrorAction SilentlyContinue | Select-Object -Last 1).Line
    $el    = [int]((Get-Date) - $start).TotalMinutes
    Write-Host "[$(Stamp)] t=${el}m drift=$drift | depth=$depth | ERR=$err CK=$ck CONS=$cons shortfall=$short | $rec"
    "$(Stamp),$el,$drift,$depth,$err,$ck,$cons,$short,$rec" | Out-File $res -Append -Encoding utf8
  }
  Write-Host "[$(Stamp)] SOAK4H DONE final drift=$(Q $driftSql) | totals ERR=$(Cnt '\[ERR\]') CK=$(Cnt 'check constraint|CK_Positions|CK_Funds') CONS=$(Cnt 'Conservation') shortfall=$(Cnt 'Short-close collateral shortfall')"
}
finally {
  if (-not $proc.HasExited) {
    Write-Host "[$(Stamp)] SOAK4H stopping server (pid $($proc.Id))"
    Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
  }
}
