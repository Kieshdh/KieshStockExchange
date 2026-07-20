# R4 §0009 Stage 2 — BotDecisionProbe + book-depth capture (Path A)

Implementation handoff for local Claude. This is a self-contained spec. Implement it end-to-end on a working branch, build, run the test suite, then run the characterization soak.

**Extended soak budget** — the user is away for at least 4 hours. Use the long-run path in Verification step 3: run a 3-4 hour characterization soak rather than the 45-60 min minimum. The longer run tightens the bootstrap 95% CIs on the 40% fire gate and lets the rare advanced (bracket) and MM cohorts accumulate statistically meaningful sample counts at production sample rates. When done, leave the analysis-script stdout and a short plain-English findings summary on the branch for review on return. **Do not land any formula fix** — Stage 2 is probe-only (see Out of scope). Commit the probe code + a separate commit for the soak artifacts/summary; do not open a PR unless asked.

## Context

Stage 1 (`MatchSymmetryProbe`, `KieshStockExchange.Server/Services/MarketEngineServices/MatchSymmetryProbe.cs`) ran a 45m / 78k-fill soak and surfaced two persistent matcher-level asymmetries (`scripts/r4_probe_analysis.py`):

- Sell-taker count is 1.27× buy-taker count.
- Buy-takers' |residual bps| vs effective taker limit is 1.19× sell-takers' (buy fills land farther from limit, i.e. better).

Both observations print at the matcher, but the matcher walk is symmetric. The asymmetry must live **upstream**.

The brief lists four candidate surfaces: (a) `AiBotDecisionService` sentiment-dynamics + value-anchor tilt, (b) `BracketCoordinator` ShortBracket/LongBracket compounded by the baked `InventoryBiasShortMult=2.0`, (c) plain limit-order placement, (d) MM quoting withdrawal.

Code audit found exactly two asymmetric formulas, both intentional and characterized:
- `_inventoryBiasShortMult` in `AiBotDecisionService.ComputeInventoryBias` — short-side threshold halved so heavy-short bias triggers easier; §0003-baked at 2.0 over 7 characterization runs.
- `Bots:Activity:WMoveDown=2.0` vs `WMoveUp=1.0` — documented leverage effect.

Every other formula is symmetric in sign. Reverting an intentional, tuned lever without empirical attribution risks regressing §10.3b's bear tail.

**Decision: Path A.** Land a flag-gated `BotDecisionProbe` plus a small extension to `MatchSymmetryProbe` that captures book depth at fill time. Run one more soak with both probes enabled. The joined data unambiguously partitions the residual asymmetry into decision-side vs book-microstructure-side. Stage 3 ships the surgical fix against the empirically identified surface.

## Approach

Two parallel additions to the probe surface, plus an analysis-script extension. Both probes obey the same off-by-default contract Stage 1 used (single `Enabled` field-read on the hot path; flag-off byte-identical).

## Files to add / modify

### NEW `BotDecisionProbe.cs`
Static class modelled on `MatchSymmetryProbe.cs` (same lock-serialized `File.AppendAllText`, same `Configure(IConfiguration)` + `ConfigureForTests(...)` seam, same silent try/catch around I/O so probe failures can't break the trading loop).

Public surface:
- `Enabled` (bool, default false), `OutputPath` (default `logs/bot-decision-probe.csv`).
- `SampleEvery` (int, default 200) and `SampleAdvanced` (int, default 1) — bracket decisions are ~3-4 orders of magnitude rarer than plain decisions per tick, so they get their own (denser) sample rate. MM rows are similarly rare; reuse `SampleAdvanced`.
- `Configure(IConfiguration)` reads `Bots:BotDecisionProbe`, `Bots:BotDecisionProbePath`, `Bots:BotDecisionProbeSampleEvery`, `Bots:BotDecisionProbeSampleAdvanced`.
- Three typed entry points: `RecordPlain(...)`, `RecordAdvancedIntent(...)`, `RecordAdvancedResult(...)`, `RecordMm(...)`.
- Sample gate is the first statement of each Record method: `if (!Enabled) return; if (Interlocked.Increment(ref _counter) % SampleEvery != 0) return;`. Separate counters per surface.
- `directionalEffective` is the post-noiseFactor product, not raw directional.

Schema:
```
timestamp,surface,bot_id,strategy,cash_prc,inv_notional,homeostatic,directional_eff,anchor,herd,buy_prob,kind_pre,bias,kind_post,qty,flip_qty,is_buy,is_market,mm_buys,mm_sells
```

### MODIFY `Program.cs`
After the existing `MatchSymmetryProbe.Configure` line, add the parallel `BotDecisionProbe.Configure(builder.Configuration)`.

### MODIFY `appsettings.json`
Add under `Bots:` at the same level as the existing Stage-1 keys:
```json
"BotDecisionProbe": false,
"BotDecisionProbePath": "logs/bot-decision-probe.csv",
"BotDecisionProbeSampleEvery": 200,
"BotDecisionProbeSampleAdvanced": 1,
"MatchSymmetryProbeDepthContext": false
```

### MODIFY `AiBotContext.cs`
Add a per-tick memoization cache:
```csharp
internal readonly Dictionary<(int userId, CurrencyType), (decimal longNotional, decimal shortNotional)>
    WatchlistInventoryNotionalCache = new();
```
Clear in `ClearTickCaches()`.

### MODIFY `AiBotDecisionService.cs`
Four hook sites; each starts with the probe's own `if (!Enabled) return;`.

1. **Refactor (perf prerequisite).** Extract `ComputeInventoryBias`'s walk into a small `WatchlistInventoryNotional(ctx, user, currency)` helper that hits the new cache. Pure refactor — flag-off bias logic is byte-identical.
2. **ChooseOrderType** (plain path): record after final buyProb computation. `RecordPlain(...)`.
3. **ComputeAdvancedDecisionAsync** (bracket cohort): two rows per advanced decision — Intent before await, Result after.
4. **ChooseMarketMakerQuote**: `RecordMm(...)`.

### MODIFY `MatchSymmetryProbe.cs`
Add `DepthContextEnabled` flag (read from `Bots:MatchSymmetryProbeDepthContext`). `RecordDepth(side, takerSideRestingDepth, makerLevelIndex)` — packed value `levelIndex * 1_000_000 + clamp(depth, 0, 999_999)`.

### MODIFY `MatchingEngine.cs`
At the existing probe call site, wrap a second `if (MatchSymmetryProbe.DepthContextEnabled)` block. Read depth via `book.SumQuantity(buySide: !taker.IsBuyOrder)`. Compute `makerLevelIndex` from a loop counter declared outside the match loop.

### MODIFY `scripts/r4_probe_analysis.py`
Read both files. Add five report blocks: (1) decision-side buy/sell ratio + hourly buckets, (2) per-strategy × inventory bucket component decomposition with bootstrap 95% CI, (3) advanced cohort cross-tab, (4) MM cohort quote-ratio, (5) matcher depth context.

### NEW `BotDecisionProbeTests.cs`
Two cheap unit tests:
1. `RecordPlain_writesExpectedRow_whenEnabled`
2. `RecordPlain_isNoOp_whenDisabled`

## Verification

1. **Flag-off determinism (5 min).** Both probes off. Assert no probe CSVs created, soak metrics match pre-patch baseline, 21/21 tests pass.
2. **Probe-on smoke (5 min).** Set all probe flags on. Assert all three surfaces (plain, advanced, mm) have non-zero rows, depth_ctx rows present, analysis script runs to completion, total probe output < 100 MB.
3. **Stage 2 characterization soak — extended (3-4 hours).** `SampleEvery=200, SampleAdvanced=1`, depth context on. Disk: confirm `logs/` has ≥1 GB free. Acceptance:
   - CK = CONS = ERR = 0 over the full run.
   - Throughput regression ≤ 10% vs a matched flag-off baseline (run a 15-20m flag-off baseline first).
   - Stability over time: decision-side buy/sell ratio stable across first vs last hour.
   - Probe self-consistency: report block #1 within ±5% of matcher-side 1.27×.
   - Surface attribution: either a surface clears the ≥40% gate (with bootstrap 95% CI) or reports "distributed".
   - **Deliverable**: leave both CSVs, full analysis stdout, and a 5-10 line plain-English findings summary on the branch.

## Out of scope

- No formula fix in Stage 2.
- No `/Tools` Excel pipeline changes.
- No new constructor parameters on `AiBotDecisionService` — probe is static.
- No changes to `BracketCoordinator` or `TradeSettler`.
