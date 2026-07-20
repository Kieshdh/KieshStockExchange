# Ultraplan handoff prompt — bracket-flip eligibility + heavy advanced-order optimization

Paste the block below to Ultraplan. The full design doc lives at
`docs/bot-bracket-flip-eligibility.md`. Empirical motivation + ruled-out alternatives are in
`docs/bot-down-drift-fix.md` §10.

---

## PROMPT (paste this)

I want you to refine and implement a coupled set of changes to the bot advanced-order surface on
the `feature/bot-market-realism-v2` branch, tip **`f18530b`** (baked weighted-week anchor +
cap-from-seed + RecentAnchor + multiplicative pressure, validated on the Step 3 soak 2026-06-12).

**You cannot build or run on my machine**, so deliver everything as a `git am`-able patch series
and a git bundle. I will apply, build, run tests, and run the soak. Do not try to launch processes
or modify outside the patch series.

The goal is **two coupled things, shipped in the SAME patch series**:

1. **Generalise bracket eligibility to all position signs with flip allowed** — `LongBracket`
   becomes eligible on `qty ≤ 0`, `ShortBracket` becomes eligible on `qty ≥ 0`. When the entry
   quantity exceeds the existing position size with sign, the entry flips the position in one
   trade. The motivation: an A/B falsification soak on 2026-06-12 showed advanced orders alone
   drive the entire `−2.3 %/2h` negative-drift residual; removing advanced flipped drift to
   `+0.54 %`. The dominant mechanism is bracket-population substrate asymmetry — long brackets
   attach freely (flat-or-long), short brackets are flat-only, and seed inventory is `~5 orders of
   magnitude` long-skewed. Path 2 (with flip) was explicitly chosen over a no-flip Path 1; the
   tradeoff is reopening the long↔short flip path that the P6 cover-clamp invariant forbids, so
   you must either carve a bracketed-only exception around the clamp or replace it with a richer
   invariant.

2. **Heavy advanced-order throughput optimization** — the SAME A/B showed advanced orders consume
   ~78 % of per-tick budget: `12,453 trades/min` advanced OFF vs `2,780 trades/min` advanced ON.
   Path 2 adds new work to the already-heavy path; if shipped naively it makes throughput worse.
   **Path 2 ON must achieve at least 6,000 trades/min in the soak (2× current baseline) — this is
   a non-negotiable perf gate.** Heavy optimization is required, not optional. The optimization
   list and perf gate are in `docs/bot-bracket-flip-eligibility.md` §5.5.

**Full design doc**: `docs/bot-bracket-flip-eligibility.md`. **Read it first.** It has:
- §1 Path 2 design (mixed-portion settlement table)
- §2 Engine touchpoints (six numbered items: BuildBracketAsync, cover-clamp helpers,
  TradeSettler mixed transitions, BracketCoordinator pool-sizing rule change, reservation
  invariants + reconciler extension, /Tools per-strategy probs)
- §3 Design extensions E1–E8 (decide-in, do not bolt on)
- §4 Five open questions for your judgment
- §5 Hard constraints (conservation, flag-off byte-identical, loop-thread only, cover-clamp
  preservation, /Tools pipeline contract, **throughput gate ≥ 6,000 trades/min**)
- §5.5 Required optimizations (eight listed: memoize IsOverBandAsync per-tick; memoize
  Fundamental / SeedPrice / GetStockPriceAsync per-tick; precompute eligible-watchlist per-bot
  per-tick; sync IsOverBand / ApplyDepthCap (drop async); ship A1b/A1c from the deferred bot-loop
  perf plan; BracketCoordinator batching with causal-order preservation; AIUser snapshot once)
- §6 Recommended soak / tuning order including a perf-only baseline (optimizations only, Path 2
  off) so the two effects can be attributed separately

**Empirical context**: `docs/bot-down-drift-fix.md` §10. The A/B numbers, the ruled-out
alternatives (so you don't re-litigate §10.3a Itô floor, §10.3d slippage depth, etc.), and the
substrate-asymmetry argument.

### Branch state you'll be working on
- Branch `feature/bot-market-realism-v2`, base commit **`f18530b`** (the bake).
- No uncommitted working-tree edits on the server config — `f18530b` is the clean baseline.
- Build clean, tests pass at `f18530b`.

### Files to read in order
1. `docs/bot-bracket-flip-eligibility.md` (this is your spec)
2. `docs/bot-down-drift-fix.md` §10 (empirical motivation)
3. `KieshStockExchange.Server/Services/BackgroundServices/Helpers/AiBotDecisionService.cs`:
   - `BuildBracketAsync` (`:446–516`) — primary target.
   - `FirstFlatStock` / `FirstLongableStock` (`:519–545`) — eligibility filters you remove.
   - `ComputeOrderQuantityAsync` + `ComputeCommittedCoverShares` / `ComputeCommittedSellShares` —
     the cover-clamp invariant you carve an exception in.
   - `BuildAdvancedAsync` (`:370–388`) — the hot decision-roll path.
4. `KieshStockExchange.Server/Services/MarketEngineServices/TradeSettler.cs` — mixed-position
   settlement path; pay attention to the P6 buy-to-cover pattern as the template for the new
   mixed-portion transition.
5. `KieshStockExchange.Server/Services/MarketEngineServices/BracketCoordinator.cs` — especially
   `OnChildFillShortAsync` and `OnStopFiringShortAsync` (the P6c pool-resize bug class — this is
   the regression risk you must protect against).
6. `docs/P6bc_DRIFT_FINDINGS.md` — the previous round of conservation drift fixes; the
   `OnChildFillShortAsync` bug pattern is *exactly* the trap to avoid.
7. `Tools/Config.py:181–197` + `Tools/Person.py` + `Tools/ExcelLayout.py` — the `/Tools` pipeline
   for the `RoundtripBiasPrc` field (E5). Note the Lateness lesson (`cc6d863`): the new column
   MUST land in `PgDBService.Misc.cs`'s four Dapper SQL constants (`AIUserCols`,
   `AIUserInsertCols`, `AIUserInsertVals`, `UpdateAIUser` UPDATE SET list) — easy to forget.
8. `KieshStockExchange.Server/Services/MarketEngineServices/Helpers/OrderBook.cs` — `SumQuantity`
   is already sync; the wrapping `ApplyDepthCapAsync` is needlessly async.

### Deliverables I want from you

- A `git am`-able patch series under `artifacts/bracket-flip-patches/0001-*.patch`. The series
  should split cleanly into:
  - `0001-perf-memoize-overband-fundamental-seed-per-tick.patch` (optimization #1, #2 from §5.5)
  - `0002-perf-sync-isoverband-applydepthcap.patch` (optimization #4, #5)
  - `0003-perf-precompute-eligible-watchlist-per-tick.patch` (optimization #3)
  - `0004-perf-aiuser-snapshot-and-coordinator-batching.patch` (optimizations #7, #8)
  - `0005-perf-batch-bracket-arm-route-a1b.patch` (optimization #6 — A1b)
  - `0006-perf-batch-bracket-arm-route-a1c.patch` (optimization #6 — A1c)
  - `0007-bracket-flip-eligibility-and-mixed-portion.patch` (Path 2 §2 engine touchpoints)
  - `0008-bracket-extensions-e1-inventory-aware-picker.patch` (E1)
  - `0009-bracket-extensions-e2-per-portion-tp-sl.patch` (E2)
  - `0010-bracket-extensions-e3-watchlist-priority.patch` (E3)
  - `0011-bracket-extensions-e4-roundtrip-sl.patch` (E4)
  - `0012-bracket-extensions-e5-roundtripbias-tools-pipeline.patch` (E5 + `/Tools` + Dapper SQL +
    EF migration + `AIUserData.xlsx` regen — single atomic patch end-to-end)
  - `0013-bracket-extensions-e6-telemetry.patch` (E6 — telemetry counters)
  - `0014-bracket-extensions-e7-regime-alignment.patch` (E7, if you implement it)
  - `0015-bracket-extensions-e8-settle-time-clamp.patch` (E8 — concurrency mitigation)
  - `0016-tests-bracket-flip-and-perf.patch` (new unit tests + a determinism test for flag-off
    byte-identical behaviour)
- A thin git bundle `artifacts/kse-bracket-flip.bundle` (base `f18530b` → tip of the series).
- A cover note `artifacts/bracket-flip-cover-note.md` like the sentiment-dynamics one. Include:
  - Locked design decisions for the 5 open questions in §4
  - Flags & defaults table (every new key in `Bots:Advanced:*`)
  - Tests results (count + which are new + the determinism test)
  - Profiling result that justifies the perf-gate prediction (you can't run it, but you should
    walk through which optimization buys what % of the 4.5× gap and what the realistic projection
    is)
  - Recommended soak / tuning order (already in §6, but customize for any deviations you made)
  - Risk list — places where regression to P6c-class bugs is plausible
- No xlsx regeneration if you can avoid it. If E5's `RoundtripBiasPrc` requires a regen, generate
  the new xlsx and include it in patch `0012`. Make the change scriptable from `Tools/` so I can
  re-run it.

### Hard constraints — these are red lines, not preferences
- **Conservation gate non-negotiable**: CK=0, CONS=0, ERR=0 over the soak. Any sign of regression
  to the P6bc `CK_Positions` class of bugs and the patch is rejected.
- **Flag-off byte-identical**: with `Bots:Advanced:BracketFlipEligibility = false` AND every new
  optimization flag default-off where applicable, behaviour is IDENTICAL to today (eligibility
  filters intact, no new RNG calls, no extra Tick work). Verified by a determinism test in
  patch `0016`.
- **Perf gate non-negotiable**: Path 2 ON soak must hit ≥ 6,000 trades/min at 70-stock / ~20k-bot
  scale on `kse_soak` (`f18530b` baseline is 2,780/min). I will measure. If your patch series
  cannot project this on paper based on the optimizations shipped, say so up front and we'll
  re-scope.
- **Loop-thread only** for new decision-side state; no async, no locks unless strictly necessary,
  no thread crossings.
- **The cover-clamp invariant is preserved for plain orders**. Any exception is bracketed-only and
  explicit, with a code comment pointing back to this plan doc.
- **`/Tools` Excel pipeline**: any new per-bot field (E5's `RoundtripBiasPrc`) flows through the
  full Excel/seed pipeline AND every Dapper SQL constant. Lateness bug (`cc6d863`) lesson — easy
  to forget.
- **No touch to matching, settlement contract, or the cash-conservation surface** beyond what §2
  engine touchpoint #3 (TradeSettler mixed-portion handling) requires.

### Open questions you must decide and justify in the cover note (full text in §4)
1. Cover-clamp invariant — carve a bracketed-only exception, or split the bracket entry into two
   physical orders?
2. Per-portion SL pool sizing — derived from `flipQty` only, or different rule?
3. P6c `OnChildFillShortAsync` formula validity under mixed-portion — verify or replace.
4. Two enum entries (`LongBracket`/`ShortBracket`) vs one parameterised — recommend keep two;
   confirm or override.
5. TTL on un-filled round-trip TPs — recommend deferral with a flag; confirm.

### Out of scope
- Removing the cover-clamp for plain orders (it stays — only bracketed flip is the exception).
- Changing the matching engine or settlement contract beyond mixed-portion handling.
- Touching the sensitivity-tuning / OverheatCap / RecentAnchor / Multiplicative config knobs
  (they are baked at `f18530b` and considered fixed for this round).
- Reworking the v2 imbalance / texture / microstructure pillars.

### Tone
Take your time. This is the same shape as the P6 brackets round — structural ramifications larger
than the patch size suggests. I'd rather see a coherent design with solid answers to the five
open questions and a credible perf-gate projection than a quick patch that breaks the conservation
gate or misses throughput. If something in the plan doesn't fit your design taste, push back in
the cover note; I will respond before you commit.
