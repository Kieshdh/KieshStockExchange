# Council decision — parallelize the bot-decision sweep (2026-07-08)

Kiesh: "we made the engine parallel — can't we do the same with the bots? If no, run it again on HOW; if
still no, I agree." Two rounds, 5 + 3 advisors. **Round 1: unanimous "feasible but not-now." Round 2
(solution-mode, committed to fleet growth): unanimous — BUILDABLE, no genuine CK/replay blocker.** So per
Kiesh's rule it is **not** a no — it's a yes with a concrete path.

## Why it's the SAFE parallelism (unlike the rejected engine∥bots)
The loop is strictly phased: `CheckTimers` (all shared-signal writers tick once) → `CollectPendingOrders`
(bots READ + compute) → `SubmitAndApplyBatch` (engine WRITES Fund/Position, sequential). The decision
sweep is temporally disjoint from every writer. Verified: per-bot RNG isolated (`AiUserRngs[id]`, eagerly
pre-seeded at load), Fund/Position frozen during collect, `GetCommitted` per-bot, order book not mutated
mid-sweep, activity/sentiment/fundamental all "tick-once-then-read." Threads would all be READERS of a
quiescent snapshot; the engine (the writer) stays single-threaded and untouched. That's why it's safe where
engine∥bots (writer racing readers → torn 128-bit decimal reads) was not.

## The one real order-dependence — fixable, not fatal
`_maxAdvancedPerTick` (the ≤50 advanced-orders/tick cap) is an in-loop RNG-gating counter: both the *set*
of winners and the RNG trajectory of capped bots depend on enumeration order. **Fix:** every due bot always
attempts the advanced decision (deterministic RNG draw), then apply the cap AFTER the sweep on the merged
list; dropped bots emit nothing. A tiny, blessed behavior change + a one-time golden re-baseline.

## The design (buildable, code-grounded)
1. **Precompute pass (kills the contention trap):** warm the 4 genuinely-shared per-stock caches
   (`Fundamental`/`SeedPrice`/`OverBandBuy`/`OverBandSell` + the `RefillGate` latch) in ONE sequential pass
   over the ~50-60 (stock,ccy) pairs, right after `ClearTickCaches()`, BEFORE the fan-out ⇒ the parallel
   region is pure-read on them (no hot-key `ConcurrentDictionary` contention — the Outsider's warning).
2. **Per-bot dicts → `ConcurrentDictionary`** (Stances/Pressures/lags/Perceived/Watchlist* etc.): each key
   is single-writer (one bot = one worker) ⇒ near-zero contention. NOT thread-local partitions (they'd
   corrupt persistent state on a fleet resize).
3. **Parallel sweep + deterministic merge:** partition the due-bot array (tagged with an **enumeration
   ordinal**) across workers → per-partition lists → concat → **stable-sort by the ENUMERATION ORDINAL,
   NOT aiUserId** → apply `_maxAdvancedPerTick` on the sorted list → hand to the UNCHANGED engine.
   - **★ Sharp catch: sort by ordinal, not aiUserId.** `GetAIUsersAsync` has NO `ORDER BY`, so today's
     serial order is undefined SQL order; sorting by aiUserId would reorder `pending` → different matching →
     NOT byte-identical. The ordinal reconstructs the exact serial append order.
4. **Compose with slot-indexing (eliminate-then-distribute):** path (A) = keep the cheap per-bot bookkeeping
   for all N, short-circuit at the existing `StaggerDue` before the EXPENSIVE compute ⇒ O(cap/N) on the
   heavy work, byte-identical (preserves the burst-draw RNG order). Path (B) = move `StaggerDue` above the
   burst draw for true O(cap/N) *iteration* — NOT byte-identical (changes burst frequency) ⇒ a separate,
   soak-validated follow-up. Ship (A).
5. **Replay-equivalence GATE:** a test asserting parallel-on == serial-on byte-for-byte (same seed/day) +
   per-bot RNG draw-counts identical, across {ParallelCollect, RefillGate, stagger} × a couple of N. Hard ship gate.

## Flags / files
`Bots:Advanced:ParallelCollect` (default-off, byte-identical off) + a `MaxDegreeOfParallelism`. Touch:
`AiBotContext.cs` (retype the 4 shared caches + per-bot dicts to Concurrent; `MidPriceCache` is dead),
`AiBotDecisionService.cs` (add `PrecomputeSharedTickCaches`), `AiTradeService.cs:1582-1667` (pre-pass +
parallel branch + merge, behind the flag), `appsettings.json`, new `BotParallelCollectEquivalenceTests.cs`.

## Phased path (single-threaded foundation ships capacity FIRST; threading is last + prod-gated)
- **Phase 0 (do first, byte-identical):** retype the 4 shared caches to `ConcurrentDictionary` + add the
  precompute pass, still inside the SERIAL loop + move the `_maxAdvancedPerTick` cap to the post-filter.
  Zero behavioral change (RefillGate off); the foundation the parallel branch sits on. Gate: replay-equal + CK=0.
- **Phase 1:** slot-materialized due buckets (O(cap/N) heavy work, single-threaded) — the headline fleet
  headroom with ZERO threading, behind the existing `Staggering` switch.
- **Phase 2:** the parallel branch behind `ParallelCollect` + the replay-equivalence test (iterate to green).
- **Phase 3:** soak flag-on at 20k (CK=0, no throughput regression).
- **Phase 4:** raise `MaxBotCap` past 20k → 50-100k, watch collect grow sub-linearly at CK=0.

## Honest caveat (the "not now" that isn't a "no")
On a COMMIT-bound prod box at 20k, collect is ~tens of ms ⇒ *threading* is noise (Amdahl). The THREADING
payoff is real only as the fleet grows toward 50-100k. BUT the single-threaded foundation (Phase 0-1:
precompute + slot-materialized buckets) cuts collect cost, is byte-identical, and is the prerequisite — it's
worth building regardless, and it's what actually raises the ceiling the scaler can't. Turn `Parallel.ForEach`
on only when a prod profile shows collect ≥ ~30% of the tick. It IS the real 20k→100k capacity lever.

## Verdict
**Buildable. No CK-safety or replay-safety blocker.** Kiesh's instinct is sound and the code already supports
it. Build the single-threaded foundation now (safe, valuable, prerequisite); gate actual threading on a prod
measurement + the replay-equivalence test.
