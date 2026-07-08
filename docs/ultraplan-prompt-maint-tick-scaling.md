# Ultraplan prompt — tick-time scaling: the tick does O(fleet)/O(book) work that grows on long runs

**Status: QUEUED for ultraplan (Kiesh, 2026-07-09), COUNCIL-VETTED design below.** Surfaced during the 48h prod
soak. PERF/scaling, orthogonal to market realism (the market is converged + good). Prod decisions locked: keep the
20k seed + fixed scaler (fixed ~10k active — proven no realism benefit from more; see `PROD_48H_TEST_PLAN.md` EXP3).
This ticket = **make the tick hold ~250ms over a multi-hour/48h run.** Kiesh: combine with the parallelization
workstream (below) since it's the same class of wall ("the tick does O(fleet) work").

## The problem (measured on prod)
Over ~13h the tick degraded **250ms → ~450ms**, entirely from the periodic **`maint` phase growing 0 → ~300ms/tick**
(`RunPeriodicMaintenanceAsync`, `AiTradeService.cs:1943`). Root: the **armed-stop pool grows UNBOUNDED** — ~1.05M
"Pending" (armed stop) orders, 88% >2h old, growing; vs only ~96k Open limits. The maint prune scans them every ~30s.

---

## WORKSTREAM 1 (PRIMARY) — the unbounded armed-stop pool. **COUNCIL-VETTED.**

### Root cause (all verified in code)
- `Status="Pending"` = **armed STOP orders** (protective stops + stop-entries), off-book until price crosses the
  trigger. Retention/cleanup explicitly LEAVE them (non-terminal; `Order.cs:369`).
- `ctx.OpenOrders` (the in-memory bot-loop working set) holds ALL non-terminal orders incl. armed stops — cold-loaded
  via `GetOpenOrdersForUsersAsync` (which returns armed stops, re-seeding arm reservations) + added at placement.
- `PruneWorstOrdersAsync` (`AiBotStateService.cs:241`) iterates `ctx.OpenOrders` for every bot every ~30s = **O(total
  orders)** = O(1.1M and rising). It only expires limits but still *iterates* the 1M stops. THIS is the maint growth.
- `Bots:OrderMaxAgeSec=1800` (limit-only age-expiry, already ON in prod) bounded the Open pool 210k→96k but did NOT
  cut maint (doesn't touch stops).
- A separate `StopTriggerWatcher` (own thread) keeps its own armed index it scans on quotes = a 2nd O(1M) cost, same root.

### Council verdict (3 advisors: first-principles / contrarian / executor)
- **Approach A — age-expire armed stops via the existing `CancelOrdersBatchAsync` (mirror OrderMaxAgeSec): REJECTED as
  first choice.** Executor found the blocker: that batch path re-reads under gate and treats `Status=Pending` as
  `!IsOpen` → returns `AlreadyClosed` → the prune would DROP the stop from OpenOrders **while leaking its arm reservation
  + leaving the StopTriggerWatcher armed = phantom-fill → CK BREAK.** Making A safe = extending the sacred settlement/
  cancel path to handle armed stops (single-order `CancelOrderAsync`→`CancelRemainderAsync` does it right; the BATCH path
  doesn't) + a work-based per-sweep cull cap for the 920k backlog + bracket/OCO-child exclusion. ~5-7h, touches CK core.
- **★ Approach B — STRUCTURAL: stop scanning armed stops in the bot loop. PREFERRED (first-principles + contrarian).**
  Armed stops are dormant triggers owned by `StopTriggerWatcher`; the bot-loop prune has no business iterating them.
  No cancel → no reservation touch, no watcher change, no mass-cancel spike → **CK-neutral.** Makes the prune O(96k
  limits), permanently, independent of stop count.
  - **Verified wrinkle (my check):** `ctx.OpenOrders` is READ by the decision path — the per-bot open-order cap
    (`AiBotDecisionService.cs:639`) + the reserved-qty aggregates (`:1562`,`:1942`). So you CANNOT just drop stops from
    OpenOrders (would change the cap count + the bot's reserved-inventory self-view = behavior change, more rejects).
  - **⇒ Implement as B2: keep `ctx.OpenOrders` intact (all reads byte-identical), add a per-bot LIMIT-ONLY index
    (`ctx.OpenLimitOrders` or a filtered view) that `PruneWorstOrdersAsync` iterates instead** — maintained on
    placement/fill/cancel/promote alongside OpenOrders. Prune becomes O(limits); decisions unchanged. CK-neutral.
- **Approach C — per-bot armed-stop CAP at placement (bound the SOURCE). ADD.** 1M stops / 20k bots = ~50/bot of
  unbounded accumulation = a modeling bug (real traders don't hold 50 open stops). Reject-at-placement past a cap
  (mirror the openCap at `:639`), CK-neutral. Bounds the DB + watcher index too (which B2 alone doesn't). First
  understand WHY 50/bot (orphaned dups? un-cancelled stops when a position closes? stop-entries that never trigger).
- **Approach D — orphan-cancel (cancel stops whose protected position is gone). Correctness, LATER.** Real CK exposure
  (cancel path) — do after B2, carefully, under ConservationProbe.

### ★ PROD FINDINGS (2026-07-09, from the interim rollout)
- The 1.16M Pending are **100% standalone stops** (`ParentOrderId IS NULL`); **0 bracket children** (brackets are
  DISABLED in the seed). So the leak is NOT brackets — it's standalone protective/entry stops.
- **THE DISEASE = a placement FIREHOSE: bots place ~570 standalone stops/MINUTE (~815k/day) that never trigger.**
  ~58/bot and rising. This dwarfs any per-order cull.
- **INTERIM SHIPPED + CK-CLEAN: `Bots:StopMaxAgeSec=600` + `StopCullMaxPerSweep=350`** (per-order safe cancel; commit
  `see feat(bots): StopMaxAgeSec`). Validated on prod: cull fires, CK=0 through the drain, releases reservations
  correctly. BUT per-order cancel is the bottleneck — it can only HOLD the pool ≈flat vs the firehose; draining the
  1.16M fast would need ~1500/sweep = multi-second tick spikes every 30s (hurts the tape). So the interim caps the
  GROWTH but gives little maint relief. **CHANGE `StopMaxAgeSec` BACK / remove the interim when the real fix lands.**
- **★ THE ROOT CAUSE (verified): additive protective stops.** `AiBotDecisionService.BuildProtectiveStopAsync` (~L954)
  places a NEW armed stop on each `StopProb`/`TrailingProb` draw and **NEVER cancels the bot's prior protective stop** —
  so a bot that keeps managing a stop STACKS them (→ ~58/bot). It's not "prob too high", it's additive accumulation.
- **★ FIX (a) — "REPLACE-OLD" (RECOMMENDED, the disease cure):** before `BuildProtectiveStopAsync` places a new
  standalone protective stop, cancel that bot's existing standalone armed stop(s) (same stock/side, or all its
  standalone armed stops) via the SAFE per-order `CancelOrderAsync` path (the one the interim validated CK-clean).
  Bounds the pool to **~1/bot (~20k)** vs 1.16M ⇒ maint scan → O(20k), retires the StopMaxAgeSec interim + its cull
  spikes, and PRESERVES behaviour (bots still actively manage one protective stop = realistic; real traders MOVE a
  stop, not stack 58). Default-off behind a flag; CK gate through a soak. Bracket children EXCLUDED (as the interim).
- **FIX (b) — lower `StopProb`/`TrailingProb`:** trivial config/multiplier, but reduces how often bots hold protective
  stops at all (less protective-stop realism) and doesn't cure the additive root. Fallback only.
- Rank: **(a) replace-old FIRST** (source cure, realism-preserving) → then B2 if the residual O(book) scan still bites.

### Recommended build order (REVISED)
**(1) Reduce stop-placement rate at the source (config/seed) — the firehose is the root; cheapest, spike-free →
(2) B2 (don't scan armed stops — CK-neutral, immediate maint relief) → (3) retire the StopMaxAgeSec interim →
(4) D orphan-correctness later.** Skip A (per-order cull) as anything but the interim. Gate every step: CK=0
(ConservationProbe + ReservationAuditor), tick holds ~250ms over a multi-hour soak, market metrics unchanged,
levers default-off/byte-identical.

## WORKSTREAM 2 — the O(bots×stocks) economy snapshot
`_economy.LogSnapshot` (in the same maint phase) is O(20k×50 ≈ 1M ops)/interval. Fixed-size (not the growth driver) but
a chunk of maint. It's telemetry ⇒ sample/incrementalize or move off the tick thread. Low risk.

## WORKSTREAM 3 — get periodic maintenance OFF the tick thread
`RunPeriodicMaintenanceAsync` runs synchronously IN the loop; even bounded, a heavy pass lands in a tick. Move the heavy
tasks to a true background worker so a maintenance pass NEVER inflates the tick. Threading-safety review needed (touches
ctx) — the biggest structural win, highest care.

## WORKSTREAM 4 (COMBINE) — parallelize / slot-materialize the collect O(N) scan
Per Kiesh, fold in **`docs/BOT_PARALLELISM_BUILD_PLAN.md`** (+ `docs/COUNCIL_DECISION_bot_parallelism.md`). Same class:
the tick does O(fleet) work. Phase 0 foundation (precompute shared caches, retype to Concurrent, `_maxAdvancedPerTick`
post-filter, byte-identical) → slot-materialized due-buckets (O(cap/N)) → parallel collect behind `Bots:Advanced:
ParallelCollect` (default-off, gated on a replay-equivalence test + prod collect ≥30% of tick). Sharp catches: merge by
ENUMERATION ORDINAL not aiUserId; `_maxAdvancedPerTick` → post-filter.

## Constraints / acceptance
CK=0 sacred; tick ~250ms held over a MULTI-HOUR run (the real test — the degradation only shows over hours); both order
pools plateau; market realism byte-unaffected (maintenance is not a decision-path change); new levers default-off.
Interim prod mitigation already on: `Bots:OrderMaxAgeSec=1800` (bounds the Open limit pool only) — superseded by this.
