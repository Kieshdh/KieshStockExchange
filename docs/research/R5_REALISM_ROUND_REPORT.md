# R5 realism round — report (2026-06-15)

Branch `feature/bot-market-realism-v2`. Goal: make the bot market feel less "arithmetic" (more realistic
candles/wicks), break the −0.43 1-min return autocorrelation, and remove unnatural order walls.

## TL;DR
- **Shipped (baked): order-wall fix** — soft round-number snap (`Bots:RoundSnapSpread=0.40`). The one
  robustly-reproducible win: across ~10 soaks it ALWAYS collapses round-grid book volume ~22%→~1% and halves
  top-level concentration (9.4%→3–4%). Conservation clean over a 90m/419k-trade validation. (commit `e4f5465`,
  code `553cfe6`.)
- **Not shipped (neutral-within-noise on their targets, kept default-off / validated defaults):** R5 anchor
  reaction-lag (B) + dead-band (C); #1 directional-loop lag; #3 slow-Hawkes clustering. All committed,
  flag-gated, byte-identical when off — available for future longer-horizon testing.
- **Structural ceiling documented:** `ret_acf_lag1 ≈ −0.43` is immune to every reaction-timing lever (R4's 19
  experiments + R5 B/C + #1). Diagnosed as ~28% bid-ask bounce + ~72% genuine flow mean-reversion.

## What we tried and what the data said

### Order walls (SHIPPED)
Root cause (not pruning, not the offset RNG): `GetLimitPriceAsync` snapped ~30% of limit orders to an EXACT
coarse round level (`$1` for ≥$100 stocks, etc.), collapsing volume onto single prices = monolithic walls.
Pruning culls by distance/age, never by clustering, so it can't declump them. Fix = `SnapToRoundNumber(price,
spread, jitter01)`: `spread>0` disperses snapped orders within ±spread·unit so volume forms a soft cluster
near the round number instead of one impassable price. Round-number *attraction* (a real stylized fact) is
preserved; only the monolith is broken.
- Measured by `scripts/wall_diag.py` (top-level share / HHI / round-grid share). Robust across every soak.

### ret_acf_lag1 = −0.43 (STRUCTURAL CEILING — stop chasing with timing)
`scripts/bounce_diag.py` splits it by comparing CLOSE (last-trade, bounces) vs VWAP (bounce averaged out):
~28% is bid-ask bounce (microstructure, only fixable via more-crossable book / non-trade-close candles),
~72% is genuine mean-reversion in the volume-weighted flow. Reaction-timing experiments that tried to break
it ALL failed: R5 anchor-lag (B) moved VWAP-AC1 from −0.31 to −0.32 (worse); #1 directional-lag likewise no
help. Combined with R4's 19 experiments, this is a structural property of the matching/flow, not a tunable.

### #3 slow-Hawkes volatility clustering (NOT baked — noise-dominated)
Pillar B (self-exciting activity, already ON, already has the leverage effect WMoveDown>WMoveUp) has a Hawkes
half-life of ~1 min (`Decay=0.99`), so `absret_acf` dies by lag-5. Hypothesis: slow the decay holding the
branching ratio (`Decay 0.995`, `WSelf 0.0045`, n=0.9) → longer clustering memory. One round showed
absret_acf_lag1 0.15→0.223 + lag5 flipping positive; a later round showed the REVERSE (0.204 without #3 vs
0.097 with). The metric's run-to-run variance (~±0.1 at 30–60m/16-stock) swamps the effect. No reliable
benefit → kept `Decay/WSelf` at their previously-validated defaults.

## Methodology notes (for next time)
- **Soaks are NOT bit-deterministic** (wall-clock dt drives the OU/regime/Hawkes AR(1) updates), so single
  45–60m / 16-stock realism scores carry large noise (composite ±~10, absret_acf ±~0.1, has_wick ±~10).
  ONLY conclusions reproducible across runs are trustworthy. Walls were; the rest mostly weren't.
- **Always compare arms IN PARALLEL** (shared machine load). Cross-round comparisons are invalid — VS's
  `ServiceHub.IndexingService` + concurrent builds/scoring stole CPU and cut soak throughput up to ~45%
  (fewer loop ticks → fewer trades → shifted every metric).
- **Max 2 parallel servers** — 3×20k bots overran Postgres `max_connections` (default 100).
- Hold builds/scoring while soaks run to keep throughput (and thus the comparison) clean.

## Artifacts
- New diagnostics: `scripts/bounce_diag.py`, `scripts/wall_diag.py`.
- Flags added (all default-off / validated-default): `Bots:RoundSnapProb/RoundSnapSpread` (spread baked 0.40),
  `Bots:AnchorReactionLag/AnchorLagMin/MaxAlpha/AnchorDeadbandPrc`, `Bots:DirectionalReactionLag/DirLagMin/MaxAlpha`,
  per-bot order-age patience factor (`BotLifetimeFactor`, active only when `OrderMaxAgeSec>0`).
- Tests: `AnchorTimingTests`, `RoundSnapTests` (150/150 pass).
