# Bot-decision parallelism — execution build plan (for 2026-07-09)

## ★★ READ BEFORE FIRING — current prod context + two hard constraints (2026-07-09)
- **Goal = CAPACITY/liquidity, NOT realism.** The market is already converged + validated on prod (cross-stock corr
  factorR2 0.244, realistic fat tails, healthy movement) at ~10k active bots. EXP3 (rotating the full 20k) was
  realism-NEUTRAL, and the project's core LLN finding is that MORE independent bots AVERAGE OUT imbalance (flatter tape,
  not richer) — the realism comes from the ROTATOR's coordinated flow, not headcount. So this project buys DEPTH/
  headroom for more real users; **when growing the cap (Phase 3-4) you MUST re-measure corr/tails and STOP if they
  dilute.** Don't expect a livelier market from more bots.
- **★ HARD PREREQUISITE for Phase 3-4 (fleet growth): the standalone-armed-stop LEAK must be fixed FIRST.** Bots emit
  a firehose of ~570 never-triggering standalone stops/MIN at 10k (additive — `BuildProtectiveStopAsync` never cancels
  the prior stop; ~58/bot piled up to 1.16M). Growing to 50k scales that to ~2,800/min ⇒ it re-creates the exact
  maint/tick blowup (the periodic prune scans the O(book) armed-stop pool) that this whole perf effort is about —
  WORSE. Fix the source first: **"replace-old"** (cancel the bot's prior standalone stop when placing a new one, via
  the safe per-order path) bounds the pool to ~1/bot. Spec + rationale = **`docs/ultraplan-prompt-maint-tick-scaling.md`**
  (Workstream 1). An interim `Bots:StopMaxAgeSec=600` (per-order cull, capped) is LIVE on prod holding the 10k pool
  ~flat, but its cull can't keep up at 50k. **Do NOT raise `MaxBotCap` in Phase 4 until replace-old (or B2) has landed
  + soaked CK-clean.**
- Fleet is currently HELD at ~10k BY DESIGN (Kiesh); this plan's Phase 0-2 (foundation + threading, byte-identical,
  default-off) is safe to build regardless — only Phase 3-4 (actually raising the cap) is gated on the above.

Full design + rationale + the "no genuine blocker" verdict = **`docs/COUNCIL_DECISION_bot_parallelism.md`**
(read it). This is the execution checklist. Goal: raise the fleet ceiling (20k → 50-100k at a low tick) by
parallelizing the read-only bot-decision sweep — the SAFE boundary (engine stays single-threaded). CK=0 +
byte-identical-replay are the hard gates. All behind `Bots:Advanced:ParallelCollect` (default-off).

**Key principle: eliminate work before distributing it. The single-threaded foundation (Phase 0-1) ships the
fleet headroom byte-identically WITHOUT threads and is the prerequisite; threading (Phase 2) is last + gated
on prod collect ≥ ~30% of the tick.** At 20k on a commit-bound box, threading is Amdahl-noise — the value is
the foundation + the future fleet-growth ceiling.

## ★ Two sharp catches (don't miss)
1. **Merge-sort by ENUMERATION ORDINAL, not aiUserId.** `GetAIUsersAsync` has NO `ORDER BY`, so serial order
   is undefined SQL order; sorting by aiUserId would reorder `pending` → different matching → NOT byte-
   identical. Tag each due bot with its enumeration ordinal and sort by that to reconstruct exact serial order.
2. **`_maxAdvancedPerTick` is order-dependent** (in-loop RNG-gating counter) → convert to a deterministic
   post-filter: every due bot attempts the advanced decision → apply the cap AFTER the sweep on the sorted
   list → dropped bots emit nothing. A tiny blessed behavior change + a one-time golden re-baseline.

## Phase 0 — foundation (single-threaded, byte-identical modulo the blessed cap change) — DO FIRST
- [ ] Retype the 4 genuinely-shared per-stock caches (`FundamentalCache`, `SeedPriceCache`, `OverBandBuyCache`,
      `OverBandSellCache`) + the per-bot state dicts (`Stances`/`Pressures`/`AnchorTiltLag`/`DirectionalLag`/
      `Perceived*`/`ReactionHold`/`BurstEndTimes`/`Committed`/`Watchlist*`) → `ConcurrentDictionary`
      (`AiBotContext.cs:127-149,64-105`). `MidPriceCache` is DEAD — delete or ignore. `AiUserRngs` already
      pre-seeded at load → can stay plain (optional Concurrent as cheap insurance).
- [ ] Add `PrecomputeSharedTickCaches(ctx)` in `AiBotDecisionService` — one sequential pass over the ~50-60
      active `(stockId,currency)` listings (ascending), warming `Fundamental`/`SeedPrice`/`IsOverBand(buy&sell)`
      + `ctx.MoverGate` (advances the RefillGate latch once/stock deterministically). Call it right after
      `ClearTickCaches()` (`AiTradeService.cs:1590`), still inside the SERIAL loop.
- [ ] Move `_maxAdvancedPerTick` (`:1656`) to the post-sort deterministic filter (catch #2).
- **Acceptance:** replay-equivalence unit test (serial-with-prepass == serial-without, modulo the blessed cap
  re-baseline) + build + full suite + a CK-clean smoke. Ships value alone (removes redundant recompute).
  RefillGate-on semantics shift lazy→eager under the prepass (both RefillGate + ParallelCollect default-off ⇒
  shipping default unaffected; the replay gate compares prepass-vs-prepass).

## Phase 1 — slot-materialized due buckets (single-threaded, the headline capacity win)
- [ ] Replace "iterate all 20k, `continue` on `StaggerDue`" with N materialized buckets by `id % Slots`,
      rebuilt on daily bot-set change → each tick iterates only the ~cap/N due bucket.
- [ ] Path (A) — keep the cheap per-bot bookkeeping (burst/quiet/interval/RecordDecision/tradeProb) for the
      full N; short-circuit at the existing `StaggerDue` BEFORE the expensive `Compute*Async`. This is
      byte-identical (preserves burst-draw RNG order) and gets O(cap/N) on the HEAVY work. **Ship (A).**
      Path (B) (move StaggerDue above the burst draw for true O(cap/N) iteration) is NOT byte-identical → a
      separate, soak-validated follow-up. Don't bundle B.
- **Acceptance:** Slots=1 byte-identical; Slots>1 == today's staggered output; CK=0; `BotPhase` collect ms
  drops ≈N-fold at fixed cap.

## Phase 2 — the parallel branch (behind `Bots:Advanced:ParallelCollect`, default-off)
- [ ] `AiTradeService.cs:1582-1667`: `if (_parallelCollect) {` materialize due array w/ enumeration ordinal →
      partition across `MaxDegreeOfParallelism` (default `Environment.ProcessorCount`) → `Parallel.ForEachAsync`
      into per-partition Lists (NOT ConcurrentBag — nondeterministic) → concat → stable-sort by ORDINAL →
      apply cap → unchanged engine `}` else the serial Phase-0/1 path.
- [ ] Ctor reads `ParallelCollect` (default false) + `MaxDegreeOfParallelism`. appsettings + Production.json.
- **Acceptance (HARD GATE):** `BotParallelCollectEquivalenceTests` — parallel-on == serial-on byte-for-byte
  (orders: side/type/stock/price/qty/ccy) + per-bot RNG draw-counts identical, across {ParallelCollect,
  RefillGate, stagger} × a couple of N values. Ships ONLY when green across the matrix.

## Phase 3 — prod validation + fleet growth
- [ ] Soak flag-on at 20k on prod: CK=0, no throughput regression (only turn threads on when prod `BotPhase`
      collect ≥ ~30% of tick — else the serial Phase-0/1 stack is the right answer, flag stays off).
- [ ] Raise `MaxBotCap` past 20k → 50-100k in steps; watch collect grow SUB-linearly, CK=0 each step.

## Files
`AiBotContext.cs` (retype caches; `:127-149,64-105`; RNG `:179-190`; RefillGate `:214-228`),
`AiBotDecisionService.cs` (add `PrecomputeSharedTickCaches`; `ComputeOrderAsync`/`ComputeAdvancedDecisionAsync`),
`AiTradeService.cs:1582-1667` (prepass + parallel branch + merge + cap post-filter), `appsettings.json` +
`appsettings.Production.json` (`Bots:Advanced:ParallelCollect`), new `KieshStockExchange.Tests/BotParallelCollectEquivalenceTests.cs`.

**First concrete step:** Phase 0 — retype the 4 shared caches to `ConcurrentDictionary` + add
`PrecomputeSharedTickCaches` after `ClearTickCaches()`, serial, byte-identical; land the replay-equivalence
test green. That's the foundation everything else sits on.
