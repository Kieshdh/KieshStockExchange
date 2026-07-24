# PROD ENV OVERRIDES — the live prod flag state (captured 2026-07-24)

**★ STATUS: first-class reference. This is the LIVE `Bots__*` env-override set actually running on prod**, captured from the
running container (`kse-server-server-1`), NOT the repo appsettings. **Effective prod config = `appsettings.json` defaults +
these env overrides** (`docker-compose.prod.yml` sets `ASPNETCORE_ENVIRONMENT=Production` + the `Bots__*` env below; env wins over
appsettings). Recapture any time with:
```bash
ssh root@159.195.149.51 'docker inspect -f "{{range .Config.Env}}{{println .}}{{end}}" kse-server-server-1' \
  | grep -iE "^Bots__|^Candles__|^ASPNETCORE_ENV" | grep -viE "PASSWORD|SECRET|KEY|CONNECTION|TOKEN|JWT" | sort
```
(secrets — DB connection string, JWT key — are also env but are NEVER captured here). Pairs with `SERVER_HOST_AND_OPS.md`
(deploy host/recipe), `FLAG_REGISTER.md` (flag lifecycle), `MARKET_BALANCING_CONFIG.md` (knob meanings).

## Live overrides (2026-07-24, ASPNETCORE_ENVIRONMENT=Production, .NET 9.0.18)

**ExogShock (news system) — ON:**
- `Enabled=true` · `MaxMagnitude=0.06` · `MagnitudeExponent=3.5` · `MinMagnitude=0.01` · `Cap=0.25` (the **news-strength CUT** — random-walk-first §1 revision, live)
- `MeanIntervalMinutes=60` · `DecayHalfLifeSec=600` · `AnchorTracksShock=true`
- `Permanence__Enabled=true` · `Permanence__AlphaMin=0.40` · `Permanence__TauMedianSec=2000` (variable-permanence news)
- `GlobalCoFire=true` · `GlobalCoFireFraction=0.15` · `GlobalCoFireNotionalFrac=0.1` · `GlobalFraction=0.25`
- `ChaserFraction=0.10` · `ChaserNotionalFrac=0.06` · `ChaserMaxNotionalFrac=0.10` · `ChaserMinIntervalSec=120`

**Mood (fear/greed) — ON:** `Enabled=true` · `TakerCoupling=true` · `ConvictionFearBid=true` · `MMWiden=true` · `PerStrategy=true`

**RegimeDrift (RegimeTaker) — ON:** `Strength=0.4` · `TakerCoupling=true` · `TakerStrength=0.12` · `TakerThreshold=0.20` · `CohortFraction=0.03`
  — **this is the "prod-like RegimeTaker" soak baseline** (replicate these in every A/B arm so the local tape matches prod).

**Misc — ON:** `MarketProbMult=1.2` · `RecentAnchor__Strength=0.05`

## ★ What is DEFAULT-OFF in prod (NOT overridden ⇒ appsettings default ⇒ off) — the pending-deploy features
None of these appear in the prod env, so all run at their default-off appsettings value:
- **F1 `Bots:Personality:SectorSizeModel`** = OFF (sector×size personality — cleared, deploy held for Kiesh batch-eyeball)
- **F5 `Bots:Sentiment:RegimeDrift:MarketPulse:Enabled`** = OFF (cleared, deploy held for Kiesh "breathe" eyeball + 3-seed guard)
- **log-sym suite** `Bots:Fundamental:GeometricBand` / `Bots:ValueAnchor:GeometricBand` / GeometricGap = OFF (built, arm only on a stable down-bias)
- **`Candles:ContinuousOpen`** = OFF (seam step7 — built, hold for Kiesh chart eyeball)
- **`Candles:ClientCache`** = OFF (client cache — client-only flag, not a server env anyway)
- Composition `TakerExp` and F2 `Bots:Activity:HotRotation` (parked) — off.

## Effective appsettings-only actives (NOT env-overridden, but ON via appsettings defaults — easy to forget)
- **`Bots:BounceReference` = "mid"** (microstructure bounce — the mid-reference candle CLOSE is LIVE in prod; this is why the
  continuous-open seam step7 is a REAL prod-candle change, not byte-identical).
- **`Candles:HLMinFillSize` = 10** (filtered-tape H/L odd-lot rule, live).
- Composition seam `TakerExp` appsettings default (check `appsettings.json` — the §composition taker share).

**Takeaway for deploys:** prod is a clean baseline with all the in-flight realism features OFF. A deploy = ship the code (default-off,
byte-identical) then optionally add the flag as a `Bots__*` env override in `docker-compose.prod.yml` to flip it on live.
