# ATTENDED-ARC PREP MAP — `AiTradeService`

> **Status: ATTENDED / CK-critical.** This class is the bot tick loop. It is only
> restructured with the owner (Kiesh) present, and every extraction is gated by a
> multi-hour CK (conservation) soak. This document is the *judgment* input for that
> attended session — read it instead of re-doing the archaeology live.
>
> Backlog directive for this class: **real-extract `BatchSubmissionService` ONLY;
> keep the tick loop + timers WHOLE (do not fragment the loop).**
>
> READ-ONLY analysis. No `.cs` file was modified to produce this map.

---

## 1. File identity

| Field | Value |
|---|---|
| Path | `KieshStockExchange.Server/Services/BackgroundServices/AiTradeService.cs` |
| Exact LOC | **2447** |
| Namespace | `KieshStockExchange.Services.BackgroundServices` |
| Declaration | `public class AiTradeService : IAiTradeService, IAsyncDisposable` |
| `sealed`? | **No** (plain `public class`) |
| `partial`? | **No** — single file, single class |
| Base type | **None.** It is **NOT** a `BackgroundService` and **NOT** an `IHostedService`. |
| Interfaces | `IAiTradeService`, `IAsyncDisposable` |
| Project | `KieshStockExchange.Server.csproj` (`net9.0`, class-lib/server; not the MAUI Windows TFM) |
| DI lifetime | **Singleton** — `Program.cs:270` `AddSingleton<IAiTradeService, AiTradeService>();` |

### How the loop is actually driven (important — no self-hosting)
`AiTradeService` does **not** host itself. A separate thin wrapper owns lifecycle:

- `KieshStockExchange.Server/Services/HostedServices/BotLoopHostedService.cs`
  (`sealed class BotLoopHostedService : IHostedService`) — `StartAsync` calls
  `_bots.StartBotAsync(ct)` gated on `Bots:AutoStart` (default false);
  `StopAsync` calls `_bots.StopBotAsync()`.

So the "background service" role is split: **lifecycle** lives in the hosted-service
wrapper; the **loop** lives here as a `Task.Run` started by `StartBotAsync`. This is a
plus for the restructure — the class is already free of `ExecuteAsync`/host plumbing.

### InternalsVisibleTo
`KieshStockExchange.Server.csproj:43` → `KieshStockExchange.Tests` (also
`DynamicProxyGenAssembly2` for Moq at line 45). So `internal` members of this class are
directly reachable from the test project.

### Tests that exercise it — the behavioural oracle (mostly INDIRECT)
There is **no test that instantiates `AiTradeService` and runs a tick.** The oracle is
thin and indirect; the real gate is the CK soak. What exists:

- **Pure statics (direct):**
  - `AiTradeService.StaggerDue(...)` → `BotStaggeringDeterminismTests.cs`
  - `AiTradeService.TimeEwmaKeep(...)` → `ImpactDecoupleDeterminismTests.cs`,
    `SmoothedPriceEwmaTests.cs`
- **Scaler via `Mock<IAiTradeService>`:** `BotScalerOnTickTests.cs`,
  `BotStrategyBreakdownTests.cs` (they mock the interface, not the loop).
- **Batch/arm equivalence (at the `OrderEntryService` layer, not this class):**
  `ArmStopBuyBatchEquivalenceTests.cs`, `ArmedStopSourceCapTests.cs` — these are the
  closest thing to a `BatchSubmissionService` oracle, but they test the **engine-side**
  batch entry route. `ArmStopBuyBatchEquivalenceTests` header explicitly notes "case 9
  (flag-off routing to the per-order path) lives in AiTradeService's [loop]" — i.e. the
  *routing* is untested at unit level.
  `ArmedStopSourceCapTests` header: "the only unit-reachable coverage of the two INLINE
  arm-result sites in AiTradeService".
- **Collect-loop equivalence (mirrored, not invoked):** `BotPrecomputeEquivalenceTests.cs`
  *re-implements* the per-bot inner body of `CollectPendingOrdersAsync` to prove the
  precompute is byte-identical — it does not call the method.

**Implication for the attended session:** unit tests will NOT catch a regression in the
extraction's wiring. The CK soak + the `_tradesPlacedThisSession`/`_failuresThisSession`
counters + `ReservationAuditor`/`ConservationProbe` are the actual safety net. Treat the
soak as mandatory, not optional.

---

## 2. THE TICK LOOP + TIMERS — the do-not-fragment core

### Anatomy
- **Entry:** `StartBotAsync` (1232) → `ResetSessionState` (1292) →
  `_runner = Task.Run(() => RunLoopAsync(_schedulingCts.Token))` (1246).
- **Loop:** `RunLoopAsync(ct)` (1338–1498). One `while (!ct.IsCancellationRequested)`
  with a whole-tick `try/catch` guard (a transient failure logs and continues — the
  fleet must not stop trading). End-of-tick `await Task.Delay(delay, ct)` paces it to
  `TradeInterval` (optionally self-correcting via `_selfCorrectingDelay`).
- **Shutdown:** `StopBotAsync` (1249) — cancels `_schedulingCts`, waits `Bots:GracefulStopMs`
  (default 8000) for a clean drain, else cancels `_engineCts` to hard-cancel in-flight
  engine work, then unsubscribes books and logs the FX-desk summary.

### Per-tick orchestration (the fixed sequence inside `RunLoopAsync`)
The order below is **load-bearing** — several passes explicitly depend on running against
"the freshest book" left by the previous pass, and the CK argument for each cohort is "it
runs *outside the matcher's locked region*, after the batch". Sequence:

1. `CheckTimers(now, ct)` (2255) — advances **external state before consumers**: FX →
   sentiment → regime → activity → news → bank → funds → priceMemory → mood (observe/score)
   → daily refresh. This is itself a mini-orchestration and must stay whole (see §5).
2. `CollectPendingOrdersAsync(now, ct)` (1873) → `(pending, advanced)`.
3. `SubmitAndApplyBatchAsync(pending, ...)` (1986) — **the plain-order batch** (matcher hot path).
4. `SubmitAdvancedAsync(advanced, ...)` (1507) — stop/trailing/bracket/short entry route.
5. `_arbitrage.RunAsync` (gated) → `_marketMaker.RunAsync` → `_rotator.RunAsync` →
   `_conviction.RunAsync` → `_jump.RunAsync` → `_bracket.DrainAsync` — the special cohorts,
   each gated by its own `bool`, each running after the batch and outside the lock.
6. `RecordTickLatency` / `RecordActionableLatency` / `Interlocked.Increment(_tickCount)` /
   `RecordActivitySample`.
7. `_scaler.OnTick(this)` → maybe `SetActiveBotCap`.
8. `_auditor.AuditAsync` (reconcile, gated to a 5-min cadence, at the **post-batch quiescent
   frame** — this timing is a correctness requirement, see §4).
9. `RunPeriodicMaintenanceAsync(now, ct)` (2306) — heavy amortized walks, deliberately kept
   OUT of `CheckTimers` and placed **after** `RecordTickLatency` so their spikes never skew
   the scaler EWMA.
10. Optional phase-timing accumulation (`Bots:PhaseTimingSeconds`).

### Why this must stay whole
- **Single scheduler / single clock.** `_ctx.TickId` and `_ctx.TickNowTicks` are stamped
  once per tick (in `CollectPendingOrdersAsync`, 1879–1880) and every bot decision this tick
  reads that one deterministic clock. A second scheduler or a re-ordered pass would change
  *which bots are due* and *what price/OpenOrders they see* → non-reproducible runs and a
  different tape. The whole determinism contract ("byte-identical when flag off") assumes one
  loop, one clock, one pass order.
- **Ordering coupling between passes.** MM quotes → rotator rotates against MM's book →
  conviction acts on the freshest book → jump walks it. These comments are contracts. Do
  not parallelize or reorder.
- **Quiescent-frame invariants.** Reconcile and maintenance run *after* the batch precisely
  because "this tick's market orders are terminal, so only resting limit reservations remain
  and the clamp is safe" (1441–1443). Move them and the reservation clamp can fire mid-flight.
- **Single-thread mutation discipline.** The loop is single-threaded on purpose. Comments
  repeatedly note "still on the loop thread (single-threaded, no new races)". Any extraction
  that introduces a second thread touching `AiBotContext` / `AccountsCache` breaks this.

### Loop's shared mutable state + synchronization
- **Cross-thread state (quote-drain thread ↔ loop thread):** `OnQuoteUpdated` (2132) runs on
  the **MarketDataService quote-drain thread** (subscribed at 1118 `_market.QuoteUpdated += OnQuoteUpdated`;
  unsubscribed in `DisposeAsync`). It mutates `_ctx.StockPrices`, `_ctx.PreviousPrices`,
  `_ctx.SmoothedPrices`, `_ctx.ReactionRefPrices` (+ their `*UpdatedUtc` dicts). These are
  `ConcurrentDictionary` by design — the loop reads them each tick. **This is the one true
  concurrency seam in the class and it is NOT part of the batch path.**
- **Explicit locks:**
  - `lock (_ctx.AiUsersByAiUserId)` — `OnlineBotCount` getter (49) vs `AiBotStateService.LoadAsync`
    repopulating on the loop thread; admin HTTP reads the count.
  - `lock (_activitySamplesLock)` — `GetActivitySamples` (1180, dashboard thread) vs
    `RecordActivitySample` (1225, loop thread).
- **Interlocked counters:** `_tickCount`, `_tradesPlacedThisSession`, `_failuresThisSession`,
  `_lastTickWorkMicros`; `Volatile` for the EWMA doubles — all published to the dashboard reader.
- **Loop-thread-only state:** the `_next*Time` scheduler clocks, the `_ph*` phase-timing
  accumulators, `_cmPrev*` commit snapshots. Single-threaded ⇒ plain fields are safe **only
  because** nothing else writes them.

---

## 3. `BatchSubmissionService` — the ONE sanctioned real-extract

### What "the batch path" is
Two distinct submission surfaces exist; be precise about which is the target:

- **A. Plain-order batch (the primary target):** `SubmitAndApplyBatchAsync` (1986–2062).
  Takes `List<(AIUser user, Order order)> pending`, calls
  `_marketOrders.PlaceAndMatchBatchAsync(orderList, ct)` (the matcher hot path), then loops
  results applying bookkeeping: failure recording (`_failures.Record`, `user.RecordError`,
  `_failuresThisSession++`), success (`_tradesPlacedThisSession++`, `_stats.RecordPlacement`),
  fill side-effects (`LastTradeAtUtc`, `_stats.AddVolume`, `_activity.RecordFill`,
  `_mood.RecordTakerFlow`), and `_state.ApplyResultToCache`.

- **B. Advanced/entry-route batch (secondary, cohesive with A):**
  `SubmitAdvancedAsync` (1507) is the dispatcher; it partitions `advanced` by kind and fans
  out to the batched entry helpers:
  - `SubmitArmBatchAsync` (1690) — `_entry.ArmStopSellBatchAsync`
  - `SubmitBuyStopBatchAsync` (1747) — `_entry.ArmStopBuyBatchAsync`
  - `SubmitBracketBatchAsync` (1581) — `_entry.PlaceBracketBatchAsync`
  - `SubmitMarketShortBatchAsync` (1627) — `_entry.PlaceMarketShortBatchAsync`
  - `SubmitAdvancedPerOrderAsync` (1784) — the per-order fallback (flag-off path)
  - `ApplyAdvancedResult` (1662) + `BuildTpLegs` (1679) — shared result bookkeeping
  - Flags: `_batchArms`, `_bracketBatch`, `_batchBuyStops`, `_batchShortOpens`,
    `_stopReplaceOld` (+ `_state.CancelPriorStandaloneStopsAsync`).

**Recommendation:** `BatchSubmissionService` should own **A + B** — everything from the
"turn a collected list into engine calls + apply results" concern. That is exactly the
`Submit*` method family (1507–1856, 1986–2062) plus `StaggerDue`/`BuildTpLegs`. It is the
most self-contained, most engine-adjacent, most independently-testable slice in the file,
and the batch-equivalence tests already live at the boundary it calls into
(`_entry.*BatchAsync`, `_marketOrders.PlaceAndMatchBatchAsync`).

### Methods/state that become `BatchSubmissionService`
- **Methods (move):** `SubmitAndApplyBatchAsync`, `SubmitAdvancedAsync`, `SubmitArmBatchAsync`,
  `SubmitBuyStopBatchAsync`, `SubmitBracketBatchAsync`, `SubmitMarketShortBatchAsync`,
  `SubmitAdvancedPerOrderAsync`, `ApplyAdvancedResult`, `BuildTpLegs`, and `StaggerDue` may
  stay here (it is a *collect* gate, not a submit concern — see caveat).
- **Injected deps the service needs:** `IOrderExecutionService _marketOrders`,
  `IOrderEntryService _entry`, `AiBotStateService _state`, `AiBotContext _ctx`,
  `BotStatsLogger _stats`, `BotFailureTracker _failures`, `BotActivityService _activity`,
  `MarketMoodService _mood`, `ILogger`, plus the batch flags (`_batchArms`, `_bracketBatch`,
  `_batchBuyStops`, `_batchShortOpens`, `_stopReplaceOld`) and `DebugMode`/`DebugUserId`.
- **Shared mutable state it writes — THE KEY COUPLING:** the three session counters
  `_tradesPlacedThisSession`, `_failuresThisSession`, and the property `LastTradeAtUtc`.
  These are read by `IAiTradeService` (dashboard) and reset in `ResetSessionState`.

### How the loop would call it
**Injected dependency (constructor-injected singleton), inline call sites unchanged in shape:**
```
if (pending.Count  > 0) await _batch.SubmitAndApplyBatchAsync(pending, token);
if (advanced.Count > 0) await _batch.SubmitAdvancedAsync(advanced, token);
```
The loop keeps ownership of *when* to call (the pass order in §2 is untouched); the service
owns *how* to submit + apply.

### Is the batch state separable from the loop's core state? — MOSTLY, with 3 flagged couplings
1. **Session counters (`_tradesPlacedThisSession`, `_failuresThisSession`, `LastTradeAtUtc`).**
   The batch path *writes* them; the loop/interface *reads* them and `ResetSessionState`
   *zeroes* them. **Resolution options:** (a) the service exposes increment methods / the
   loop passes an accumulator and folds results after the call; or (b) move the counters into
   a tiny shared `BotSessionCounters` object injected into both. Option (b) is cleanest and
   keeps the "reset on Start" semantics in one place. **This is the single most important
   coupling to design before touching code.**
2. **`_ctx` (`AiBotContext`) is shared with the collect loop.** The batch path reads it via
   `_state.ApplyResultToCache(_ctx, result)` and `_state.NoteArmedStopPlaced(_ctx, ...)`.
   `_ctx` is already a shared, injected object — passing it into the service is fine and
   changes no ownership (it is not *loop-private* state). Not a blocker.
3. **`StaggerDue`.** Called from `CollectPendingOrdersAsync` (collect concern), not submit.
   Keep it with the loop/collect side (or in a shared static helper). Do not let it migrate
   into `BatchSubmissionService` just because it is `internal static`.

Everything else the batch path touches (`_stats`, `_failures`, `_activity`, `_mood`,
`_entry`, `_marketOrders`) is an already-independent helper/service — clean to inject.

**Verdict: separable, with the session-counter seam as the one real design decision.**
No hidden loop-private mutable state is entangled in the batch path beyond those counters.

---

## 4. CK / conservation touch-points (do-NOT-fragment seams)

The loop drives every order that mutates funds/positions. CK-relevant seams:

- **Plain batch submission** → `_marketOrders.PlaceAndMatchBatchAsync` (2062-region /
  line 1992). This is the matcher's locked region; the whole batch is one atomic
  place+match+settle group. **Do not split a batch across calls** — conservation depends on
  the group transaction. (`SettlementEngine`/`MatchingEngine` own the tx.)
- **Advanced/entry route** → `_entry.*BatchAsync` and the per-order `_entry.Place*` calls
  (1690–1856). Each call "owns its own book→fund→position gates while the loop holds none."
  **The invariant is: the loop must hold no gate when calling these.** An extraction must
  preserve that — the service calls the engine exactly as the loop does today; it must NOT
  wrap them in any new lock.
- **Cohort passes** (arb/MM/rotator/conviction/jump) settle through the same engine and are
  covered by `ConservationProbe`/`ReservationAuditor` **only because** they run after the
  batch, outside the matcher's lock (1383–1416). These are **out of scope** for the
  `BatchSubmissionService` extract — leave them in the loop.
- **Reconcile pass** — `_auditor.AuditAsync(_reconcileClamp, ct)` at the post-batch quiescent
  frame (1445–1453). The `_reconcileClamp` correctness argument ("only resting limit
  reservations remain") depends on this running after the batch and after `RecordTickLatency`.
  **Do not move it into any extracted service; it stays in the loop, in place.**
- **`ResetSessionState` fundamentals re-seed** (1312–1319): `_bank.Reset` **before**
  `_funds.Reset` (funds read bank estimates), news/rotator/conviction/jump/priceMemory/fxDesk
  resets. This ordering is a conservation-adjacent contract — do not reorder.

**CK touch-point count (seams the extraction must not fragment): 5** — (1) plain-batch group
tx, (2) advanced entry-route gate ownership, (3) cohort-passes-after-batch placement,
(4) post-batch reconcile frame, (5) the `ResetSessionState` reseed ordering.

---

## 5. Config coupling & cross-class coupling

### Config-heaviness — YES, this shares the AiBotDecisionService pattern (and exceeds it)
- **`AiTradeService`: 431 `_configuration.GetValue(...)` calls**, almost all in the
  constructor (269–~1120, ~850 lines — the class's single largest region). It builds ~30
  collaborators inline (sentiment, regime, activity, mood, funds, news, profiles, injector,
  arbitrage, MM, bank, rotator, conviction, jump, economy, auditor, scaler, stats,
  failures, priceMemory…) and configures a dozen static probes (`ImpactHoldProbe`,
  `ChaserProbe`, `ArmedStopCapProbe`, `RefillThrottleProbe`, `ActivityCompositionProbe`,
  `MarketMakerProbe`, `JumpsProbe`, `EngineCommitMetrics`).
- For comparison, `AiBotDecisionService` has **152 `private readonly` fields** (the "~155
  fields" the backlog references). `AiTradeService` is the *other half* of the same
  config-heavy bot-cluster smell: DecisionService hoards **fields**, TradeService hoards
  **construction/config wiring**.
- **This is a real second carve target** (a `BotServiceFactory` / composition-root extraction
  of the giant constructor) — but it is **out of scope** for the sanctioned
  `BatchSubmissionService` extract and should be a *separate* attended arc. Flag it; don't
  bundle it.

### `AiBotDecisionService.LoopStartUtc` / Reset — YES, direct cross-class coupling
- `ResetSessionState` sets **`_decisions.LoopStartUtc = TimeHelper.NowUtc();`** (1311) — arms
  the "open taker ramp" uptime clock inside `AiBotDecisionService`. The Trade loop **owns the
  lifecycle** of that clock.
- The heavy per-bot compute lives in `AiBotDecisionService`
  (`ComputeOrderAsync`, `ComputeAdvancedDecisionAsync`, `PrecomputeSharedTickCaches`,
  `CanPlaceMoreOrder`), called from `CollectPendingOrdersAsync`. So the collect half of the
  loop is tightly bound to DecisionService; **DecisionService is NOT part of the batch path**
  and should not be touched by the `BatchSubmissionService` arc.
- **Sequencing implication for the bot-cluster restructure:** `AiTradeService` (loop owner,
  sets `LoopStartUtc`, drives resets) sits *above* `AiBotDecisionService` (compute) in the
  dependency order. Any DecisionService restructure must preserve the `LoopStartUtc` setter
  and the `Reset`/`Precompute`/`Compute*` surface the loop calls. Do the **BatchSubmission
  extract first** (it touches neither DecisionService's fields nor its clock), then the
  DecisionService field-cluster arc, then (optionally) the constructor/composition-root arc.

### Other notable cross-class coupling
- **`MarketMoodService` is a shared singleton** injected into both `AiTradeService` and
  (per `Program.cs:266`) elsewhere — the loop *drives* its `Tick/Observe/Score/UpdateLaggedGlobal`
  cadence in `CheckTimers`. Mood scoring lives in the loop's timer pass, not the batch path.
- **`ResetSessionState` fans out `.Reset(...)` to ~12 collaborators** — the loop is the reset
  conductor for the whole bot subsystem. Keep this centralized.

---

## 6. Partial-carve proposal (concern groups, loop kept whole)

Rough line ranges (approximate; keep the loop + timers + reset intact):

| Group | Methods / regions | ~LOC | Extract? |
|---|---|---:|---|
| **G1 — Batch submission (SANCTIONED)** | `SubmitAndApplyBatchAsync`, `SubmitAdvancedAsync`, `SubmitArmBatchAsync`, `SubmitBuyStopBatchAsync`, `SubmitBracketBatchAsync`, `SubmitMarketShortBatchAsync`, `SubmitAdvancedPerOrderAsync`, `ApplyAdvancedResult`, `BuildTpLegs` | ~490 (1507–1782, 1784–1856, 1986–2062) | **YES → `BatchSubmissionService`** |
| G2 — Tick loop core (DO NOT FRAGMENT) | `RunLoopAsync`, `CollectPendingOrdersAsync`, `RecordTickLatency`, `RecordActionableLatency`, `StaggerDue` | ~360 (1338–1498, 1873–1984, 2064–2088, 1865–1871) | **NO — keep whole** |
| G3 — Timers / periodic maintenance (DO NOT FRAGMENT) | `CheckTimers`, `RunPeriodicMaintenanceAsync`, `SumBotInventory`, probe-drain logging, `OnQuoteUpdated`, `SmoothedPrice/Reaction/RecentReturn` helpers, `LogPhaseTiming`, `LogReactionRefDivergence` | ~430 (2089–2299, 2306–2428, 2132–2254) | NO — leave in place (loop-owned cadence) |
| G4 — Lifecycle / session reset | `StartBotAsync`, `StopBotAsync`, `ResetSessionState`, `DisposeAsync` | ~160 (1232–1334, 2440–2446) | NO — orchestrator identity |
| G5 — Construction / config wiring | the ctor (269–~1120) + probe config | ~850 | **Future separate arc** (`BotServiceFactory`/composition root); NOT this arc |
| G6 — Telemetry surface (delegating props) | export/CSV/failure/ledger/economy/sentiment pass-throughs (99–128), activity samples | ~150 | Optional trivial group; low value, leave |

Only **G1** is a real-extract this arc. G5 is the tempting-but-deferred second target.

---

## 7. Recommended ATTENDED SEQUENCE (one extraction per CK soak)

> Principle: one behaviour-preserving move per multi-hour CK soak; flags default unchanged;
> point the live client at the arm under test; the counters + `ReservationAuditor` +
> `ConservationProbe` are the pass/fail signal (unit tests won't catch a wiring regression).

**Step 0 — Pre-work (no soak):** design the **session-counter seam** (§3, coupling #1).
Recommend a small injected `BotSessionCounters` (holds `TradesPlaced`, `Failures`,
`LastTradeAtUtc`) shared by loop + service, reset in `ResetSessionState`. Get owner sign-off
on this shape before writing code — it is the one non-mechanical decision.

**Step 1 — Extract `BatchSubmissionService` (the sanctioned arc).**
- Move G1 (§6) into `BatchSubmissionService`, constructor-injected into `AiTradeService`.
- Inject: `_marketOrders`, `_entry`, `_state`, `_ctx`, `_stats`, `_failures`, `_activity`,
  `_mood`, the batch flags, `DebugMode/DebugUserId`, `BotSessionCounters`, `ILogger`.
- Loop call sites become `_batch.SubmitAndApplyBatchAsync(...)` / `_batch.SubmitAdvancedAsync(...)`
  — pass order in §2 unchanged; loop still holds no gate at the call.
- **Preserve:** batch group-tx atomicity, gate-free entry-route calls, all bookkeeping
  side-effects (fills → activity/mood/stats), and the flag-off per-order fallback routing.
- **Gate:** multi-hour CK soak — `ConservationProbe` and `ReservationAuditor` clean over the
  full soak; `_tradesPlacedThisSession`/`_failuresThisSession` trajectories match a
  pre-extract baseline arm; no new CK_ events. **This is the arc the backlog authorizes; stop
  here unless the owner explicitly extends scope.**

**Step 2 (only if owner extends scope) — Constructor / composition-root carve (G5).**
- Extract the ~850-line constructor's collaborator construction into a `BotServiceFactory` /
  composition root; `AiTradeService` receives already-built collaborators.
- Behaviour-neutral by construction (same objects, same config), but touches every probe and
  the reset fan-out → still CK-gated.
- **Gate:** CK soak + a byte-identical-tape check vs baseline (determinism contract).

**Step 3 (separate arc, separate owner session) — `AiBotDecisionService` field-cluster.**
- Not this class. Sequenced *after* Steps 1–2 because the loop sets `LoopStartUtc` and drives
  DecisionService's `Reset`/`Precompute`/`Compute*` surface — that surface must be frozen
  first. Its own CK + `BotPrecomputeEquivalenceTests` are the gate.

**Do NOT, in any step:** fragment `RunLoopAsync`, add a second scheduler/clock, move the
reconcile or maintenance passes out of the loop, reorder the cohort passes, or introduce a
new thread touching `AiBotContext`/`AccountsCache`.
