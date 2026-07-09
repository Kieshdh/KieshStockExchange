# Ultraplan prompt — tick-time scaling: the tick does O(fleet)/O(book) work that grows on long runs

**Status: QUEUED for ultraplan (Kiesh, 2026-07-09), COUNCIL-VETTED design below.** Surfaced during the 48h prod
soak. PERF/scaling, orthogonal to market realism (the market is converged + good). Prod decisions locked: keep the
20k seed + fixed scaler (fixed ~10k active — proven no realism benefit from more; see `PROD_48H_TEST_PLAN.md` EXP3).
This ticket = **make the tick hold ~250ms over a multi-hour/48h run.** Kiesh: combine with the parallelization
workstream (below) since it's the same class of wall ("the tick does O(fleet) work").

## ★★ MEASUREMENT GATE RESULT (2026-07-09, prod, collect-split telemetry `52372e3`) — SELECTS THIS PATCH
Deployed the collect-split BotPhase telemetry to prod and measured two pinned operating points (ActiveCap 10k vs 5k
via the admin API, AutoScale off, MaxBotCap left at 20k), 6 steady windows each, **CK=0 both**:

| field (avg) | ~10k | ~5k |
|---|---|---|
| Tot ms | 110 (maint-off floor ~63) | 83 (floor ~52) |
| collect ms | 7.2 | 5.0 |
| batch ms | 29.8 | 24.4 |
| recon ms (bursty) | 6.0 | 4.2 |
| **maint ms (bursty, the growth term)** | 37 (0–100) | 22 (0–54) |
| collect-split pre / pass / compute ms | 0.17 / 4.70 / 2.34 | 0.18 / 3.19 / 1.62 |
| eligible / due | 2964 / 230 | 2388 / 160 |
| µs/bot (pass) / µs/due (compute) | 1.58 / 10.2 | 1.33 / 10.15 |

**Verdict against the pre-registered gate:** µs/due is dead-flat (10.2≈10.15) and µs/bot ~flat, so the projection is
valid — BUT collect is only **6–7% of the tick** at both points, the N's scale sub-linearly with the cap (eligible
0.81×, due 0.70× for a 2× cap cut ⇒ slope ambiguous), and even a generous linear projection of collect to 50k (~35ms)
stays far under 40% of a multi-hour tick that maint drags to 300–450ms. ⇒ **collect is NOT the bottleneck; the unbounded
`maint` scan is. Build Workstream 1 (stop-leak: replace-old → B2), NOT the collect-parallelism (Workstream 4 is
DEFERRED — measurement-justified: parallelizing the cheap 7ms collect can't touch the disease).**

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
(ConservationProbe + ReservationAuditor), tick recovers toward ~250ms — validated by a CLOSELY-WATCHED 45m soak
with monitors @15/30/45m (B2 makes maint O(limits) by construction, so relief is immediate + structural and a
full multi-hour run isn't needed to prove it — the interim was the thing that needed hours), market metrics
unchanged, levers default-off/byte-identical.

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
CK=0 sacred; tick recovers toward ~250ms — VALIDATED by a CLOSELY-WATCHED 45m soak with monitors every 15min (maint ms
drops + holds, CK=0 through the drain, stop pool trending down, tick recovered). B2 makes the prune O(limits) by
construction ⇒ relief is immediate + structural, so a 45m watched soak suffices (the multi-hour concern was the
StopMaxAgeSec INTERIM, a cull that couldn't keep pace — the structural fix doesn't need hours to prove); both order
pools plateau; market realism byte-unaffected (maintenance is not a decision-path change); new levers default-off.
Interim prod mitigation already on: `Bots:OrderMaxAgeSec=1800` (bounds the Open limit pool only) — superseded by this.

---

## ★★ W1 SOAK RESULT + WORKSTREAM 1b (B3) — the reload is a SECOND O(pool) cost (2026-07-09)
W1 (replace-old + B2, `Bots:StopReplaceOld`+`Bots:PruneLimitOnly` on, `StopMaxAgeSec=0`) shipped default-off (`884fd28`),
flags flipped on prod (`cf4df62`), soaked 45m with monitors @15/30/45m:
- **CK=0 at every checkpoint.** replace-old is conservation-safe.
- **Pool draining:** 1,184,129 → 1,160,582 → 1,138,139 (−46k/45m, ~1.5k/min net) — replace-old works, but drains the
  1.14M backlog only GRADUALLY (~12h), because a bot only cancels its prior stop when it re-arms that same (stock,side).
- **maint (ms):** 185/242/233 → 350/230/202 → 227/125/83. **tick 208–540ms — did NOT reach ~250ms.**
- **adv phase elevated 42–104ms** (the replace-old per-order cancels, while the backlog is still large).
- **KEY:** maint still TRACKS the pool size (falls as the pool falls) — the opposite of what B2 promised (flat-low
  regardless of pool).

**DIAGNOSIS (confirmed in code):** B2 made `PruneWorstOrdersAsync` limit-only, BUT the maint phase
(`RunPeriodicMaintenanceAsync`, `AiTradeService.cs:2018`) ALSO calls `RefreshAssetsAsync` (`AiBotStateService.cs:103`)
on the reload interval, which re-fetches ALL open orders incl the ~1.18M armed stops via `GetOpenOrdersForUsersAsync`
and rebuilds `ctx.OpenOrders` (`~:126-149`) = **O(pool)**. That periodic reload is the residual pool-proportional maint
cost B2 doesn't touch ⇒ the tick only recovers as the pool drains (slow), not immediately.

**CONSTRAINT (the wrinkle B2 already respected):** you can't just skip armed stops on reload — the decision path reads
`ctx.OpenOrders` for the per-bot open-order cap (`AiBotDecisionService.cs:639`) + the reserved-qty aggregates
(`:1562`, `:1942`), which include armed stops (a bot's reserved inventory includes its armed sell-stops). Dropping them
changes those reads = behavior change.

**B3 = the next fix (design + implement, default-off/byte-identical, CK-neutral):** get the tick to ~250ms WITHOUT
waiting ~12h for the drain. Candidate directions (pick/blend/beat):
  (i) **Reload not O(pool):** don't re-fetch/rebuild armed stops every cycle (they change rarely) — load once +
     maintain incrementally, or reload limits every cycle and armed stops far less often; keep the reserved-qty/cap
     reads correct (maintain the armed-stop reserved aggregates separately so `OpenOrders` needn't hydrate every stop).
  (ii) **Drain faster:** a bounded background cull of the OLD standalone armed stops (the `StopMaxAgeSec` interim shape)
     running ALONGSIDE replace-old, so 1.14M drains in a couple hours not ~12h — bounded per sweep, SAFE per-order
     cancel, CK-gated.
  (iii) whatever the council finds is the true minimal fix.
Acceptance: tick recovers toward ~250ms — validated by a closely-watched 45m prod soak, monitors @15/30/45m (maint ms
drops + holds INDEPENDENT of the stop pool, CK=0, pool draining). W1 stays on prod meanwhile (CK-safe + draining).

---

## ★★ COUNCIL VERDICT (2026-07-09, 4 advisors) — the forward plan: bound the SOURCE + incremental aggregates + off-thread
The council (First-Principles / Contrarian / Executor / Outsider) reviewed the whole arc and converged on a **root
reframe**: the flag-stack (replace-old + B2 + B3) treats SYMPTOMS of two diseases —
1. **Unbounded source** — bots hoard ~58 dormant standalone stops/bot (~570/min firehose that never triggers). Real
   venues never hold that; "most stops die young." B3 exists only because the pool is allowed to reach ~1.18M.
2. **Materialized-view-recompute anti-pattern** — the in-memory open-order dict (`ctx.OpenOrders`) is a matview
   REBUILT FROM SCRATCH every ~60s by `RefreshAssetsAsync` = O(pool). B2/B3 only lower the coefficient on the pool
   term; neither removes it.

**Three structural end-state moves (ranked):**
1. **★ BOUND THE SOURCE (the disease cure — Contrarian headline, Executor + Outsider concur).** A per-bot armed-stop
   CAP at placement (mirror the open-order cap) + per-(bot,stock,side) NETTING (replace-old is a partial version) +
   optional TTL. Bounds the pool to a small multiple of bots ⇒ **O(pool) stops mattering ⇒ B3/LeanReload's whole
   justification largely evaporates.** Cheapest, highest-leverage, CK-neutral. First understand WHY ~58/bot (initial
   pile + arms on new (stock,side) that replace-old doesn't catch).
2. **★ INCREMENTAL AGGREGATES / IVM (the clean architecture — First-Principles headline, Outsider #1).** The decision
   path needs only a per-bot open-order COUNT + reserved-qty SUM from armed stops — both are monoids (arm +1/+qty,
   fill/cancel −1/−qty). Maintain them incrementally at the arm/fill/cancel sites the loop already owns; then the
   reload never hydrates armed stops AND never recomputes their aggregates. `RefreshAssetsAsync` degrades to a rare
   reconciliation/drift-audit, not the hot path. Collapses maint O(pool)→O(events/tick). **Retires B3's count query.**
3. **★ W3 — MAINT OFF THE TICK THREAD (the invariant — First-Principles #2, Outsider #3).** Double-buffered immutable
   snapshot the tick swaps atomically; the heavy sweep (reload, prune, economy LogSnapshot) runs on a background
   context so a pass NEVER inflates a latency-critical tick. This is the invariant that ENDS the whole class:
   **the ~1s tick must never do O(fleet) work.** Subsumes W2 (the economy `LogSnapshot` O(bots×stocks) rides the same
   off-thread mechanism).

**On B3 (the split):** Contrarian = don't even ship it (bound the source and it's unnecessary). First-Principles +
Executor = ship it, it's built + unblocks the soak + VALIDATES the diagnosis (does an O(limits) reload flatten maint?).
**Resolution: B3 = validated INTERIM (soaking now), NOT the architecture.** Its soak data tells us the maint FLOOR once
the pool term is gone — which directly informs whether source-cap alone would have sufficed.

**Executor's operational sequence:** B3 soak green (maint <~120ms + slope~0 vs pool + CK=0 across the 3 monitors) →
consider baking the 3 flags → **RE-PROFILE** the maint sub-phases for the next dominant one (expect W2 economy
LogSnapshot) → then fire the SOURCE-CAP (cheap, always-worth) and/or W2/W3 per the profile. **DROP faster-drain** —
the ~30h drain self-heals and is a non-problem (doubly so once the source is capped). W4 (collect parallelism) stays
deferred (collect 6-7% of tick).

**⇒ NEXT ULTRAPLAN (after the B3 soak reads):** the SOURCE-CAP (per-bot armed-stop cap + netting) as the disease cure,
then INCREMENTAL AGGREGATES to retire the periodic recompute, then W3 to move maint off the tick. This supersedes the
"faster-drain" and pure-B-workstream framing above.
