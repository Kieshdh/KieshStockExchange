# Ultraplan — Bank price-estimate + Rotational bots + per-strategy telemetry

**Branch:** `feature/bot-market-realism-v2`. **Design source of truth:** `docs/BANK_ESTIMATE_ROTATIONAL_BOTS_PLAN.md` (read it first — this prompt is the build spec; that doc is the why + the two council rounds). Two coupled features + a telemetry deliverable + a reseed cohort. **All new behavior DEFAULT-OFF and byte-identical when off.** Deliver one patch; the two features sit behind SEPARATE flags so they can be soak-validated independently.

## Non-negotiable guardrails
- **CK=0 (share + cash conservation) is sacred.** Every new order path rides the existing OrderEntry→Match→Settle so conservation holds; never mutate Fund/Position outside the engine.
- **Single-threaded, COMMIT-BOUND ~1s tick loop, ~20k bots.** New per-tick work must be O(bots) at worst and use the existing BATCHED routes — no per-bot DB round-trips, no per-tick book scans beyond what the cohort already needs.
- **Byte-identical when all flags off** — gate every new code path; unset config ⇒ the legacy engine, bit-for-bit. Add determinism/CK unit tests.
- Full test suite green. `AIUser.strategy` is already persisted from the seed xlsx.

## Local pre-step (do first, it's a shared-model change)
`KieshStockExchange.Shared/Models/AIUser.cs` line 9: extend the enum to `..., MarketMakerHouse = 6, Rotator = 7`. Grep for exhaustive `switch`/`switch`-expressions on `AiStrategy` and add the `Rotator` arm (default to "behaves like a normal bot / no-op" everywhere except the new decision pass).

## FEATURE 1 — Bank price-estimate (flag `Bots:BankEstimate:Enabled`, default false)
A "dominant house analyst" that periodically republishes a per-stock fair-value estimate; the price is guided toward it by the EXISTING elastic value-anchor.

**New `BankEstimateService.cs`** (Helpers, ~70 lines). Per-stock published estimate updated on a **Poisson-timed** republish (NOT fixed cadence — a clockwork sector lurch is a rigged tell). Mean interval `Bots:BankEstimate:PoissonMeanIntervalSec` (default ~30). On each republish for a stock:
```
estimate = Alpha·centeredSentiment + (1-Alpha)·estimate_prev + sectorTerm + varianceDraw
```
- `centeredSentiment` = the per-stock sentiment signal **with its rolling mean subtracted** (ZERO-MEAN it — the sentiment has a positive skew from the DipBuy floor; an uncentered input makes estimates ratchet up = a new up-drift source). This is a hard requirement.
- `sectorTerm` = a per-sector shared drift, sector = `stockId % SectorCount` (`Bots:BankEstimate:SectorCount`), reusing the existing sector-pulse grouping. This is what makes a whole sector re-rate together.
- `varianceDraw` = small idiosyncratic noise so the estimate is sometimes WRONG (lags/overshoots price — the price-vs-estimate GAP is the tradeable feature; if price always converges it's just a leash). Scale by `Bots:BankEstimate:WrongnessFraction`.
- **Anti-pump cap:** clamp the per-republish Δestimate so the cohort cannot close the price→estimate gap faster than the republish interval (prevents oscillation/synthetic pump).
- Expose `Func<int,double> BankTarget` returning the estimate as a **fractional deviation from seed**.

**FundamentalService anchor pivot** (`FundamentalService.cs`): point the OU reversion target at the bank estimate instead of the raw seed. In `Tick()` the OU pulls toward `target = _bankTarget?.Invoke(sid) ?? seed`. **Clamp the estimate target to `seed·[1 ± _band·0.8]`** using the EXISTING `_band` field (default 0.12) and a named constant `EstimateTargetInnerBand = 0.8m`, so the estimate stays interior to the existing hard band and the OU can still diffuse (a target parked at the hard band kills diffusion variance — THE pathology to guard). This clamp must be the ONLY place the estimate is bounded into the anchor. Byte-identical when `_bankTarget` is null/unwired.

**Do NOT build a separate revision-shock source** — the Rotator cohort IS the taker delivery of the estimate.

## FEATURE 2 — Rotational bots (flag `Bots:Rotator:Enabled`, default false)
A new cohort on the **`ArbitrageDecisionService` template** — a separate pass in the main tick loop, `AiStrategy.Rotator = 7`. New `RotatorDecisionService.cs` (~180 lines). The cohort stays ~fully invested and rotates capital toward the bank estimate.

**Signal / ranking (PIN — do not improvise):**
```
score = 0.6·gap + 0.25·dir + 0.10·idio + 0.05·global
  gap  = (estimate − price) / estimate      // % deviation, cross-stock comparable — NOT price units
  dir  = (estimate_t − estimate_{t−N}) / estimate_t   // slow estimate velocity
  idio = a small SLOW per-bot idiosyncratic sentiment (per-bot personality; keeps them from perfect lockstep)
  global = the global sentiment signal (weighted DOWN — the bots barely care about it)
```
Rank the bot's watchlist by `score`. **Sell** the bottom ~20% (over/disfavored) it holds, **buy** the top ~20% (under/favored), via **aggressive MARKET (taker) orders**. No cash band (fully invested, rotate).

**CK-safe execution (PIN):** two sequential BATCH passes per tick — `PlaceTrueMarketSellBatchAsync` FIRST (sells settle, cash returns), THEN `PlaceTrueMarketBuyBatchAsync`. The buy pass **re-reads FRESH `AvailableBalance = TotalBalance − ReservedBalance` per bot AFTER the sells settle** and caps order size to it — never snapshot pre-sell cash into the buy sizing (partial sell-fills would otherwise trip reservation failures / CK). Sell-before-buy = the same-tick funding race is designed out; batched = no perf spike.

**Runtime flow valve `Bots:Rotator:ParticipationFraction` (default 0.10):** each tick, of the cohort whose `DecisionInterval` has elapsed, only `ceil(eligible × ParticipationFraction)` actually fire (shuffle eligible, take the first K — O(1), no per-bot reads). This is the primary correlation dial, swept in soak. `DecisionInterval` (per-bot, from the seed) is a second load lever.

**Seed (reseed-only, Tools/):** `Config.py` `ROTATOR_COHORT_SIZE = 200`, `ROTATOR_DECISION_INTERVAL = (5, 15)`; wire the count into the `JUMP_AGGRESSOR_USER_ID_OFFSET` expression (offset chain: house + arb + MM + rotator + jump — a missing term crashes the seed via UserId divergence). `Person.py` `make_rotator_bot()` mirroring `make_market_maker()` but **equal-distribution start** (equal holdings across ALL stocks so it always has inventory to sell to fund a rotation), self-funded, watchlist = whole board, strategy 7. `GenerateAIUsers.py` appends the cohort like arb/MM.

## MM cohort seed (Tools/, reseed-only)
`Config.py` `MARKET_MAKER_COHORT_SIZE = 12` (candidate — the human will locally perf-soak the 70-book board and may drop to 8), `RequoteThresholdBps = 50`. `make_market_maker()` already exists; just flip the count. Stays inert behind `Bots:MarketMaker:Enabled`.

## Per-strategy telemetry (deliverable — scaffold it; it's the validation gate)
Extend `BotEconomyTelemetry.LogSnapshot()` (already walks every bot once = O(bots), all in-memory — NO extra DB I/O). Replace the binary arb sub-total with a `Dictionary<AiStrategy, StratBucket>` accumulator: cash USD, shares USD, bot count, trades (`user.TotalTradesThisSession`). Add `_seedWealthByUserId` captured at `Reset()` for portfolio-Δ vs seed; win-rate = fraction of bots with current wealth > seed. Emit a second `BotStratPerf @ HH:mm: MM=+1.2% TF=-0.8% Rot=-0.1% ...` line (gate `Bots:StrategyTelemetry:Enabled`, default true). Add a `Tools/strategy_perf_report.py` that cold-joins `Fund + Position + AIUser.strategy` (SQLite) with candle-CSV prices for the full post-soak report (return, volume-share, passive-hold benchmark, taker-flow share per strategy).

## Config keys (appsettings.json, all default the OFF/legacy value)
`Bots:BankEstimate:{Enabled=false, Alpha, PoissonMeanIntervalSec=30, WrongnessFraction, SectorCount}`, `Bots:Rotator:{Enabled=false, ParticipationFraction=0.10}`, `Bots:StrategyTelemetry:Enabled=true`.

## Tests + validation gate
- Determinism/byte-identical-off tests for BankEstimate + Rotator (unset ⇒ legacy engine).
- A CK/share-conservation test for the Rotator sell-then-buy batch (incl. a partial-sell-fill fixture proving the buy pass reads post-sell balance).
- The estimate-target clamp test (estimate + price stay inside the hard band; OU still diffuses).
- Full suite green.
- **Human validates by soak (this patch is default-off):** estimate-alone soak (price tracks estimate, bounded, CK=0), then +Rotator at PF 0.10 (CK=0, per-strategy telemetry sane), then sweep PF for correlation. Do not enable anything by default.

## The 6 pins (a can't-test agent WILL get these wrong — honor them exactly)
1. Rotator score formula + weights + gap normalized as `(estimate−price)/estimate` + market orders + top/bottom-20%.
2. Estimate→OU clamp reuses existing `_band`, `seed·[1 ± _band·0.8]`, only clamp site.
3. Zero-mean the sentiment input to the estimate (no up-ratchet).
4. Buy pass reads FRESH AvailableBalance after sells settle.
5. Poisson republish + anti-pump per-revision-Δ cap.
6. `AiStrategy.Rotator = 7` enum + exhaustive-switch arms.
