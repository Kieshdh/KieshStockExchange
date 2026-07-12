# Reseed Runbook — candle-preserving re-anchor reseed

The standing procedure for reseeding the bot population while keeping full price history.
Prod-proven 2026-07-12 (2.36M candles preserved, join gap ≤0.16%). Attended-only.
Post-reseed transient measured on that event (before the mitigations below): first-30min
excursions p50 2.5% / p90 4.2% / max 17% — the mitigations attack exactly that.

## Steps (in order)

1. **Backup**: `pg_dump` the prod DB to `/var/backups/kse/`, verify size + gzip integrity.
2. **Export actual prices** (Kiesh directive 2026-07-13): dump each listing's last 1-min candle
   close to `Tools/current_prices.csv` (`stock_id,currency,price`). The generator overlays these
   over the hardcoded `Config.py` seed prices, so bot portfolios are BORN consistent with the
   market they wake up in — this kills the net-imbalance root of the re-valuation transient
   (holdings sized at old prices + marked at new prices = every bot's cash-band homeostasis
   fires at once).
3. **Regenerate the seed workbook**: `py Tools/GenerateAIUsers.py` (both AIUserData.xlsx copies).
4. **Stop the server**, run `scripts/reanchor.sql` (anchors `StockListings.SeedPrice` +
   `StockPrices` to the last fine-candle close; minimal truncate — admin survives).
5. **Subset seed** via the temp server (`Bots__AutoStart=false`, `docker compose run --rm
   --no-deps`): users → ai-profiles → holdings (see `scripts/reanchor-reseed.ps1`).
6. **Restart with the open taker ramp armed** (env override on the box, NOT baked):
   `Bots__Activity__Composition__OpenRampMin=10`, `OpenRampStaggerMin=8` — for the first
   ~10-18 min taker orders convert to resting limits with prob 1−ramp, per-stock staggered onset.
   Remove the override at the next quiet deploy (it is inert after the ramp window but keep
   config clean). Rationale: council 5/5 — a blackout stores the imbalance and releases it
   synchronized; the ramp bleeds it out while the book deepens.
7. **Gap-fill the downtime hole** (after the first post-restart candles exist):
   `psql -v cutover="'<stop timestamp UTC>'" -f scripts/reanchor-gapfill.sql` — flat
   zero-volume candles at the prior close across the hole (1-min only; higher TFs cascade).
8. **Gates**: CK=0 at 15m/1h; continuity query (first trades vs anchors, expect ≤~1%);
   Cnv row live; eyeball the chart wall at +60min — excursions should start at visibly
   different times (the stagger), no bar-over-bar move >~6%.
9. **Rollback**: restore the dump + `git checkout <pre-reseed tip>` + rebuild.

## Post-reseed checklist

- Re-measure `Candles:HLMinFillSize` threshold if the wick filter is on (fill-size
  distribution shifts with the population — see the appsettings comment).
- Re-register any demo accounts (public `/api/auth/register`) — the reseed wipes them.
- Watch the first 30 min for the transient profile; if the tail (17%-class outlier)
  survives the ramp + price injection, the staged follow-up is a temporary LULD-style
  band (±8% → normal over 45 min) per the seam council's Outsider.
