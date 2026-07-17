# Prod-faithful soak launcher. Sets the EXACT prod market-character env (from docker-compose.prod.yml,
# 2026-07-16 news+market-char epoch) so a local A/B screen reproduces the live market, then hands off to
# kse-balance-soak-p.ps1. Pass candidate-lever overrides as -Override "Key=Value" (repeatable) to A/B a
# single lever against the prod baseline. Run SOLO (one at a time) or with a >=4-min stagger.
#
#   pwsh scripts/prod-soak.ps1 -Db kse_wk_base -Minutes 45 -Port 5081
#   pwsh scripts/prod-soak.ps1 -Db kse_wk_cash22 -Minutes 45 -Port 5081 -Override "Bots__CashInjection__IntervalMinutes=22"
param(
  [string]$Db = "kse_wk_soak",
  [int]$Minutes = 45,
  [int]$Port = 5081,
  [int]$SampleEverySec = 300,
  [string[]]$Override = @()
)

# --- Prod market-character env (docker-compose.prod.yml server block) ---
$prod = @{
  "Bots__Mood__Enabled"                        = "true"
  "Bots__Mood__TakerCoupling"                  = "true"
  "Bots__Mood__ConvictionFearBid"              = "true"
  "Bots__Mood__PerStrategy"                    = "true"
  "Bots__Mood__MMWiden"                        = "true"
  "Bots__RecentAnchor__Strength"               = "0.05"
  "Bots__Sentiment__RegimeDrift__Strength"     = "0.5"
  "Bots__MarketProbMult"                       = "1.35"
  "Bots__ExogShock__Enabled"                   = "true"
  "Bots__ExogShock__MeanIntervalMinutes"       = "60"
  "Bots__ExogShock__DecayHalfLifeSec"          = "600"
  "Bots__ExogShock__MinMagnitude"              = "0.01"
  "Bots__ExogShock__MaxMagnitude"              = "0.12"
  "Bots__ExogShock__MagnitudeExponent"         = "2.5"
  "Bots__ExogShock__Cap"                       = "0.25"
  "Bots__ExogShock__AnchorTracksShock"         = "true"
  "Bots__ExogShock__ChaserFraction"            = "0.10"
  "Bots__ExogShock__ChaserNotionalFrac"        = "0.06"
  "Bots__ExogShock__ChaserMaxNotionalFrac"     = "0.10"
  "Bots__ExogShock__ChaserMinIntervalSec"      = "120"
  "Bots__ExogShock__GlobalFraction"            = "0.25"
  "Bots__ExogShock__GlobalCoFire"              = "true"
  "Bots__ExogShock__GlobalCoFireFraction"      = "0.15"
  "Bots__ExogShock__GlobalCoFireNotionalFrac"  = "0.1"
  "Bots__ExogShock__Permanence__Enabled"       = "true"
}
foreach ($k in $prod.Keys) { Set-Item -Path "env:$k" -Value $prod[$k] }

# --- Candidate-lever overrides (the A/B variable) ---
foreach ($o in $Override) {
  $i = $o.IndexOf("=")
  if ($i -lt 1) { continue }
  $k = $o.Substring(0, $i); $v = $o.Substring($i + 1)
  Set-Item -Path "env:$k" -Value $v
  Write-Host "[prod-soak] override $k=$v"
}

$ErrorActionPreference = 'Continue'   # soak harness native-stderr trips PS exit-1 otherwise
& "$PSScriptRoot\kse-balance-soak-p.ps1" -Db $Db -Tmpl kse_soak_seed -Port $Port -Minutes $Minutes -SampleEverySec $SampleEverySec
