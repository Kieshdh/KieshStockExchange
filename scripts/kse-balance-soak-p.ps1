# Parameterized soak (parallel-capable): reset $Db from $Tmpl, launch the server on $Port, sample
# drift/depth/conservation every interval, kill at the end. Mirrors kse-balance-soak.ps1 but takes
# -Db/-Tmpl/-Port so two instances can run side by side for A/B (e.g. nudged vs un-nudged BuyBias).
# Realism flags are passed via the caller's environment (Bots__*).
param(
  [int]$Minutes = 150,
  [int]$SampleEverySec = 600,
  [string]$Note = "parallel soak",
  [string]$Db = "kse_soak",
  [string]$Tmpl = "kse_soak_seed",
  [int]$Port = 5080
)
$ErrorActionPreference = "Stop"
$root = "C:\Users\kjden\source\repos\Kieshdh\KieshStockExchange"
$exe  = "$root\KieshStockExchange.Server\bin\Debug\net9.0\KieshStockExchange.Server.exe"
$pg   = "kieshstockexchange-postgres-1"
$conn = "Host=localhost;Port=5432;Database=$Db;Username=kse;Password=kse-dev"
$driftSql = "$root\scripts\balance-drift.sql"
$depthSql = "$root\scripts\balance-depth.sql"
$ts  = Get-Date -Format "yyyyMMdd-HHmmss"
$log = "$root\logs\soakP-$Db-$ts.log"
$res = "$root\logs\soakP-$Db-results-$ts.csv"
function Stamp { (Get-Date -Format "HH:mm:ss") }
function Q($f) { (Get-Content $f -Raw | docker exec -i $pg psql -U kse -d $Db -tA 2>$null | Where-Object { $_ -match ',' } | Select-Object -First 1).Trim() }
function Cnt($pat) { (Select-String -Path $log -Pattern $pat -ErrorAction SilentlyContinue | Measure-Object).Count }

Write-Host "[$(Stamp)] [$Db] resetting from $Tmpl"
docker exec $pg psql -U kse -d postgres -c "SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname='$Db' AND pid <> pg_backend_pid();" | Out-Null
docker exec $pg psql -U kse -d postgres -c "DROP DATABASE IF EXISTS $Db;" | Out-Null
docker exec $pg psql -U kse -d postgres -c "CREATE DATABASE $Db TEMPLATE $Tmpl;" | Out-Null

Write-Host "[$(Stamp)] [$Db] launching server on port $Port -> $log"
$env:KSE_DB_CONNECTION_STRING = $conn
$env:ASPNETCORE_ENVIRONMENT   = "Development"
$env:ASPNETCORE_URLS          = "http://localhost:$Port"
$proc = Start-Process -FilePath $exe -PassThru -WindowStyle Hidden `
        -WorkingDirectory "$root\KieshStockExchange.Server" `
        -RedirectStandardOutput $log -RedirectStandardError "$log.err"
"timestamp,elapsedMin,drift,depth,ERR,CK,CONS,shortfall" | Out-File $res -Encoding utf8
try {
  $ready = $false
  for ($i = 0; $i -lt 200; $i++) {
    Start-Sleep -Seconds 3
    if ($proc.HasExited) { throw "[$Db] server exited during warm-up (see $log.err)" }
    if (Select-String -Path $log -Pattern "starting bot loop" -Quiet -ErrorAction SilentlyContinue) { $ready = $true; break }
  }
  if (-not $ready) { throw "[$Db] server never reached bot loop" }
  Write-Host "[$(Stamp)] [$Db] READY - soaking $Minutes min ($Note)"
  $start = Get-Date; $deadline = $start.AddMinutes($Minutes)
  while ((Get-Date) -lt $deadline) {
    Start-Sleep -Seconds $SampleEverySec
    $drift = Q $driftSql; $depth = Q $depthSql
    $err = Cnt "\[ERR\]"; $ck = Cnt "check constraint|CK_Positions|CK_Funds"; $cons = Cnt "Conservation"; $short = Cnt "Short-close collateral shortfall"
    $el = [int]((Get-Date) - $start).TotalMinutes
    Write-Host ("[{0}] [{1}] t={2}m drift={3} // depth={4} // ERR={5} CK={6} CONS={7} shortfall={8}" -f (Stamp),$Db,$el,$drift,$depth,$err,$ck,$cons,$short)
    "$(Stamp),$el,$drift,$depth,$err,$ck,$cons,$short" | Out-File $res -Append -Encoding utf8
  }
  Write-Host ("[{0}] [{1}] DONE final drift={2}" -f (Stamp),$Db,(Q $driftSql))
}
finally {
  if (-not $proc.HasExited) {
    Write-Host "[$(Stamp)] [$Db] stopping server (pid $($proc.Id))"
    Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
  }
}
