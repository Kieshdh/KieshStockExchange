# R4 realism-experiment runner.
#
# Pattern: temporarily apply a config override to appsettings.json (Bots:* keys), run a 45m soak,
# score it, log the result, then restore appsettings.json. Designed to be called repeatedly in a
# script that builds up a comparison table.
#
# Usage:
#   scripts/r4_experiment.ps1 -Tag "exp_inertia_on" `
#     -Overrides @{ "Bots.Imbalance.Inertia" = "true" } `
#     [-Minutes 45] [-WindowMin 50] [-Db kse_soak] [-Tmpl kse_soak_seed] [-Port 5080]
#
# Overrides: hashtable of jq-style dotted paths → string values. The script edits the file in
# place using a tiny Python helper for correctness with the nested JSON. Restores after.

param(
    [Parameter(Mandatory = $true)] [string] $Tag,
    [Parameter(Mandatory = $true)] [hashtable] $Overrides,
    [int] $Minutes = 45,
    [int] $WindowMin = 50,
    [string] $Db = "kse_soak",
    [string] $Tmpl = "kse_soak_seed",
    [int] $Port = 5080,
    [string] $LogFile = "logs/r4_experiment_log.csv"
)

$ErrorActionPreference = "Stop"
$root = "C:\Users\kjden\source\repos\Kieshdh\KieshStockExchange"
$appsettings = "$root\KieshStockExchange.Server\appsettings.json"
$ts = Get-Date -Format "yyyyMMdd-HHmmss"
$ts_human = Get-Date -Format "yyyy-MM-dd HH:mm:ss"

# Backup original
Copy-Item $appsettings "$appsettings.bak" -Force

try {
    # Apply overrides via Python (handles nested JSON safely)
    $overridesJson = ($Overrides | ConvertTo-Json -Compress)
    Write-Host "[$ts_human] [$Tag] applying overrides: $overridesJson"
    $py = @"
import json, sys
path = r"$appsettings"
overrides = json.loads(r'''$overridesJson''')
with open(path) as f:
    data = json.load(f)
for dotted, value in overrides.items():
    parts = dotted.split(".")
    node = data
    for p in parts[:-1]:
        node = node.setdefault(p, {})
    raw = value
    if raw.lower() == "true": node[parts[-1]] = True
    elif raw.lower() == "false": node[parts[-1]] = False
    else:
        try:
            v = int(raw)
            node[parts[-1]] = v
        except ValueError:
            try:
                v = float(raw)
                node[parts[-1]] = v
            except ValueError:
                node[parts[-1]] = raw
with open(path, "w") as f:
    json.dump(data, f, indent=2)
print("ok")
"@
    $py | python -
    if ($LASTEXITCODE -ne 0) { throw "Override apply failed" }

    # Rebuild
    Write-Host "[$ts_human] [$Tag] rebuilding server"
    Push-Location $root
    dotnet build "KieshStockExchange.Server\KieshStockExchange.Server.csproj" 2>&1 | Select-Object -Last 3
    if ($LASTEXITCODE -ne 0) { throw "Build failed" }

    # Run soak
    Write-Host "[$ts_human] [$Tag] launching $Minutes-min soak (port $Port, db $Db)"
    & "scripts\kse-balance-soak-p.ps1" -Minutes $Minutes -SampleEverySec 300 -Db $Db -Tmpl $Tmpl -Port $Port -Note "exp $Tag"

    # Score realism
    Write-Host "[$ts_human] [$Tag] scoring realism"
    $scoreOut = & python "scripts\r4_realism_score.py" --db $Db --window-min $WindowMin --label $Tag 2>&1
    $scoreOut | Out-File "logs\r4_exp_${Tag}_${ts}.txt" -Encoding utf8

    # Extract composite score from output
    $composite = ($scoreOut | Select-String -Pattern "Composite realism score:\s+([\d.]+)").Matches.Groups[1].Value
    if (-not $composite) { $composite = "?" }
    Write-Host "[$ts_human] [$Tag] composite score: $composite"

    # Append to CSV log
    if (-not (Test-Path $LogFile)) {
        "timestamp,tag,overrides,minutes,composite,output_file" | Out-File $LogFile -Encoding utf8
    }
    "$ts_human,$Tag,$($overridesJson -replace ',',';'),$Minutes,$composite,logs/r4_exp_${Tag}_${ts}.txt" | Out-File $LogFile -Append -Encoding utf8

    Pop-Location
} finally {
    # Restore
    Copy-Item "$appsettings.bak" $appsettings -Force
    Remove-Item "$appsettings.bak" -Force
    Write-Host "[$ts_human] [$Tag] appsettings.json restored"
}
