# Attended-Arc Prep Map: `OrderExecutionService`

**Purpose of this doc:** pre-load the owner's eventual ATTENDED restructure session with *judgment*, not
archaeology. This class is the order orchestrator and is **CK-critical** — it drives every reserve → match →
settle → release cycle and owns the group-commit transaction machinery. It is restructured ONLY with the
owner present, **one extraction per multi-hour CK soak**. Read this before the session; execute against the
gates in §7.

---

## 1. File identity

| Field | Value |
|---|---|
| Path | `KieshStockExchange.Server/Services/MarketEngineServices/OrderExecutionService.cs` |
| Exact LOC | **2712** (`wc -l`; 2713 lines counting the trailing newline) |
| Namespace | `KieshStockExchange.Services.MarketEngineServices` |
| Declaration | `public sealed class OrderExecutionService : IOrderExecutionService` — **sealed**, **NOT partial**, one interface (`IOrderExecutionService`, defined in `KieshStockExchange.Shared/Services/MarketEngineServices/Interfaces/IOrderExecutionService.cs`), **no base class** |
| Co-located private types | `DeferredGroup` (record struct, L1252), `MatchRecord` (record, L1477), `GroupOutcome` (class, L1479), `MakerRollback` (struct, L2345), `InnocentBuyMakerRollback` (struct, L2360) |
| .csproj | `KieshStockExchange.Server/KieshStockExchange.Server.csproj` — SDK-style, globbed `**/*.cs` auto-include (no `<Compile>` items). **New partial `.cs` files and a new `GroupCommitCoordinator.cs` need NO csproj edit.** |
| InternalsVisibleTo | `KieshStockExchange.Tests` and `DynamicProxyGenAssembly2` (Moq) — csproj L43/L45. The `internal static RollbackRejectedFillsCore` is already test-visible. |

### Regions (structure)
| Region | Lines | Contents |
|---|---|---|
| Services and Constructor | 24–154 | 13 injected services + gate config (`_groupGate`, `_currencyGates`, `_groupCommit`…) + ctor + `GateFor`, `CollectAffectedUsers`, `FireBracketHooksAsync` |
| Order Execution and Matching | 156–606 | `PlaceAndMatchAsync`, `MatchAndSettleAsync`, `PlaceBracketAsync`, `ArmStopAsync`, `PromoteStopAsync`, `CancelOrderAsync`, `ModifyOrderAsync`, `ModifyStopAsync` (the single-order paths) |
| Batch Operations | 608–2321 | `PlaceAndMatchBatchAsync` + **the group-commit machinery** (`RunGroupWithRecoveryAsync`, `RunGroupTxAsync`, `ApplyGroupPostCommit`, `RunGroupCommitShardsAsync`, `RunCurrencyShardAsync`, `RecoverFailedGroupAsync`) + `CancelOrdersBatchAsync`, `ArmStopBatchAsync`, `ArmStopBuyBatchAsync`, `PlaceBracketBatchAsync`, `PlaceMarketShortBatchAsync`, `ProbeF1SameUserBuyerSeller` |
| Helpers | 2323–2712 | `BuildOrdersById`, `RollbackRejectedFills` + `RollbackRejectedFillsCore` (static), `RollbackMatch` (static), `ReleaseReservationInline`, `TryParseDriftedUserId` |

### The behavioural oracle (16 test files construct/exercise this class)
`GroupCommitEquivalenceTests`, `GroupCommitCrashTests`, `GroupCommitSharedPositionEquivalenceTests`,
`GroupCommitSharedPositionFillTests`, `PerCurrencyGroupGateEquivalenceTests`, `MarketShortBatchEquivalenceTests`,
`MarketShortBatchFillEquivalenceTests`, `BracketBatchEquivalenceTests`, `ArbBatchLegsEquivalenceTests`,
`ArmStopBatchEquivalenceTests`, `ArmStopBuyBatchEquivalenceTests`, `ArmedStopSourceCapTests`,
`StopLeakTests`, `StopLeanReloadTests`, `MatcherStatusRollbackTests`, and (indirectly) `ShareConservationTests`.

**The two oracle families the attended session leans on:**
- **`GroupCommitEquivalenceTests`** (+ the `PerCurrencyGroupGate` / `SharedPosition` / `MarketShort` /
  `Bracket` / `Arb` equivalence variants): the flag-OFF vs flag-ON path must produce **byte-identical
  persisted rows + reservation-ledger tuples** for the same batch. The reservation LEDGER is the oracle. This
  is the exact test shape any GroupCommitCoordinator extraction must keep green.
- **`GroupCommitCrashTests`**: reconciles the **durable DB rows alone** across the crash window (cache is
  blind to the savepoint-release → root-commit gap). This is the test that proves the tx-unit stays
  all-or-nothing — it is the one that breaks first if the post-commit apply is fragmented from the tx body.

> **Stale-doc flag for the owner:** the XML comment on `RollbackRejectedFillsCore` (L2375-2382) cites
> `Tests.RollbackRejectedFillsSelfTest` as the reason the core is static. **No such test file exists**
> (grepped the whole `KieshStockExchange.Tests` tree — zero hits for `RollbackRejectedFills`). The static
> core's real oracle today is the equivalence + conservation soaks, not a dedicated unit test. If the owner
> wants the static extraction guarded cheaply, writing that missing self-test first is the highest-leverage
> pre-step.

---

## 2. THE GROUP-COMMIT TRANSACTION UNIT (the do-NOT-fragment core)

This is the heart of the CK criticality and the target of the `GroupCommitCoordinator` extraction. The unit
has **three phases that must stay coherent**, and the code already draws a hard line between the durable-tx
body and the post-commit apply.

### Where the transaction body runs vs where the post-commit apply happens

**Concurrency wrapper — `RunGroupWithRecoveryAsync` (L948–1008):** acquires `_groupGate` (global Npgsql-pool
guard) then, when enabled, the inner per-currency `GateFor(currency)` semaphore in a fixed **global→currency**
order (no AB/BA). Calls `RunGroupTxAsync`; on settle failure runs `RecoverFailedGroupAsync` inline (default)
or defers it to a `failedSink` (group-commit). Releases the gates in reverse in `finally`.

**Transaction BODY — `RunGroupTxAsync` (L1016–1192):** the atomic reserve→match→settle→release unit. Lock
order is **book → user gates → tx** (the money-conservation lock discipline):
1. `WithBookLockAsync(stockId, currency)` (L1041) — book lock outermost.
2. In-memory match loop over the group's orders (L1046-1063), capturing pre-match status into `groupScope`.
3. Compute gate users (fill buyers+sellers + open non-limit takers), `AcquireUserGatesAsync` (L1089) — keys
   sorted inside `AccountsCache` so parallel groups can't AB/BA; held across settle **and** commit.
4. `BeginTransactionAsync` (L1092) → `SettleTradesNoTxAsync` (L1097) → `RollbackRejectedFills` + cancelled-maker
   `UpdateAllAsync` (L1108-1118) → `UpsertOrder` / `CancelRemainderAsync` for remainders (L1128-1142) →
   `groupTx.CommitAsync` (L1144) → `committed = true`.
5. **catch (L1147):** `groupTx.RollbackAsync(CancellationToken.None)` → `RollbackMatch` in reverse record
   order → `RestoreCacheSnapshots(groupOrdersById, groupScope)` → rethrow. Transient `40P01`/`40001` are
   retried up to `MaxGroupTxAttempts` (L1166) from clean restored state.

**POST-COMMIT APPLY — `ApplyGroupPostCommit` (L1200–1246):** side-effects that are **only valid once the
group's writes are durably committed**: `TrackNewPosition` (registers new positions in the cache),
`NotifyOrdersMutated` (order-cache/UI push), `results[...] = Success` (per-order result stamp), and registry
cleanup (drop Filled-zero-reservation orders). **This block reads/pushes cache state that must not exist ahead
of the DB.**

### WHY these must stay one unit — the CK invariant

The invariant is **reserve → match → settle → release is atomic, AND the post-commit apply is gated on
durability.** Two distinct hazards:

1. **Inside the tx body:** the book mutation, the per-user gate hold, the settle writes, and the rollback path
   are one indivisible reserve/settle/release. Splitting the settle from its rollback, or releasing the gate
   before the commit, opens the P2 money-conservation race (a concurrent parallel group interleaving a
   non-atomic Fund/Position write) — the exact bug the `book → gates → tx` order was built to close.
2. **Across the commit boundary:** `ApplyGroupPostCommit` must run **after** the durable commit, never before.
   Under group-commit the savepoint "commit" (RELEASE) is **not durable** until the shard's single root
   commit, so the apply is explicitly **deferred** via `deferPostCommit=true` (L1179-1187) into a
   `DeferredGroup` buffer, and `RunCurrencyShardAsync` replays it **only after** `_db.RunInTransactionAsync`
   confirms the root commit (L1331-1337). If the chunk rolls back, the deferred groups' cache mutations are
   instead **restored** (`RestoreCacheSnapshots`, L1327-1328) and their orders recovered on a fresh tx
   (L1342-1344), leaving cache == DB.

### Method boundaries a `GroupCommitCoordinator` extraction would use

The coordinator is a genuine **collaborator class** (not a partial) that owns the whole group-commit unit.
Move this coherent set **together**:

| Member | Lines | Role |
|---|---|---|
| `RunGroupWithRecoveryAsync` | 948–1008 | concurrency gate wrapper + inline/deferred recovery dispatch |
| `RunGroupTxAsync` | 1016–1192 | **the tx body** (book→gates→tx→commit + rollback + retry) |
| `ApplyGroupPostCommit` | 1200–1246 | **the post-commit apply** (durability-gated side-effects) |
| `DeferredGroup` | 1252–1256 | deferred-apply buffer record |
| `RunGroupCommitShardsAsync` | 1264–1282 | shard groups by currency, parallel |
| `RunCurrencyShardAsync` | 1292–1354 | one root tx / one fsync per chunk; applies OR restores deferred groups |
| `RecoverFailedGroupAsync` | 1373–1475 | failed-group cancel + Phase 1.5/1.6 release (its own recovery tx) |
| `MaxGroupTxAttempts` / `IsTransientConflict` / `RetryBackoffMs` | 1358–1364 | retry policy |
| `MatchRecord` / `GroupOutcome` | 1477–1484 | per-group match bookkeeping |
| Gate fields + `GateFor` | 39–102 | `_groupGate`, `_currencyGates`, `_perCurrencyGates`, `_defaultCurrencyGroupBudget`, `_groupCommit`, `_groupCommitMaxBatch`, `_config` |

**Dependencies the coordinator needs injected:** `_db`, `_books`, `_matching`, `_settlement`, `_accounts`,
`_ledger`, `_registry`, `_orderCache`, `_logger`, plus the two static helpers `RollbackMatch` and
`RollbackRejectedFills(Core)`. It does **not** need `_marketData`, `_notifications`, `_bracket`, `_validator`
— those stay on OES (Phase 4 tick/fill publish + bracket hooks happen in the batch entry methods, after the
coordinator returns fills).

**RISK of fragmenting tx-body from post-commit apply:** if the extraction leaves `ApplyGroupPostCommit`
callable independently of (or before) the durable commit — or drops the `deferPostCommit` gate — a chunk
rollback leaves `TrackNewPosition` / `NotifyOrdersMutated` / `results=Success` applied while the DB rolled
back → **cache/UI ahead of DB**, and the crash test's durable-row reconcile diverges from the live cache.
The extraction is safe **only** if `RunGroupTxAsync` and `ApplyGroupPostCommit` move together with the
`deferPostCommit` flag, the `DeferredGroup` buffer, and `RunCurrencyShardAsync`'s "apply after commit / restore
after rollback" fork intact. **Owner call:** confirm the coordinator exposes ONE public entry (e.g.
`RunGroupsAsync(groups, results, ct)`) that internally chooses per-group vs shard/group-commit and NEVER
surfaces `ApplyGroupPostCommit` as a separately-callable seam.

**Verdict: GroupCommitCoordinator CAN be extracted as one whole transaction-unit** — the tx-body↔post-commit
seam is already explicitly coupled through `deferPostCommit`/`DeferredGroup`. The extraction is a large
collaborator move (~410 LOC of machinery), not a partial carve, and its single failure mode is exposing the
post-commit apply outside the durability gate.

---

## 3. `.Batch` partial — the batched submission route

These are the batch-entry members that could move to `OrderExecutionService.Batch.cs` **byte-identically**
(globbed csproj, add `partial` to the class). This partial holds the batch **entry** methods; the group-commit
machinery from §2 goes to the `GroupCommitCoordinator` collaborator instead (see the sequencing note below).

| Member | Lines | Notes |
|---|---|---|
| `PlaceAndMatchBatchAsync` | 609–939 | the main batched place: Phase 1 validate → 1.5 share reserve → 1.6 fund reserve → 2 bulk insert tx → 3 group runners → 4 tick/fill publish |
| `CancelOrdersBatchAsync` | 1486–1677 | per-(stock,ccy) group cancel; book→gates→tx; `ReleaseReservationInline` |
| `ArmStopBatchAsync` | 1694–1849 | §A1a batched SELL-stop arm (reserve+insert only, no match) |
| `ArmStopBuyBatchAsync` | 1866–2045 | §A1b batched BUY-stop arm (GATED fund reserve + insert) |
| `PlaceBracketBatchAsync` | 2059–2156 | §0005 batched bracket: per-parent reserve + bulk leg insert + per-parent match |
| `PlaceMarketShortBatchAsync` | 2170–2280 | Slice 2 batched flat shorts (no 1.5/1.6; collateral at fill) |
| `ProbeF1SameUserBuyerSeller` | 2293–2320 | short-classification precondition probe (log-only) |

**Sequencing caveat (owner call):** the backlog wants BOTH a `.Batch` partial AND a real-extracted
`GroupCommitCoordinator`, and they **overlap** — the group runners (`RunGroup*`, `ApplyGroupPostCommit`,
`RunCurrencyShard`, `RecoverFailedGroup`, the group types) currently sit inside the Batch region. Clean
division: **GroupCommitCoordinator takes the group machinery; `.Batch.cs` takes the entry methods above.** Do
the coordinator extraction FIRST (it removes ~410 LOC from the Batch region), THEN the `.Batch` partial carve
is a smaller, purely mechanical move of the entry methods. Doing the partial first would just have to be
re-split when the coordinator lands.

**Byte-identical constraint:** the batch methods deliberately DUPLICATE structure rather than share
(`PlaceMarketShortBatchAsync` is a documented ~40-line copy of the plain batch's tail, L2166-2169: *"Do not
DRY these together"*). A `.Batch` partial move must preserve that duplication verbatim — the whole point is
that the always-on plain method stays literally unedited (the flag-off byte-identical guarantee).

---

## 4. `RejectedFillRollback` — already static, cleanly extractable

**It is already done in-place.** `RollbackRejectedFillsCore` (L2383–2589) is `internal static` and takes every
collaborator as a **parameter** — `IAccountsCache accounts`, `IReservationLedger ledger`, `ILogger logger`,
`int? debugUserId` — plus the pure data args (`matches`, `book`, `rejected`, `ordersById`). It touches **no
instance field**. The thin instance wrapper `RollbackRejectedFills` (L2368–2373) just forwards `_accounts`,
`_ledger`, `_logger`, `DebugUserId`.

**Coupling check:** none to instance state. It reads/writes only its arguments (`book`, `ordersById`, the
maker/taker `Order` objects) and calls `accounts.GetPosition` / `ledger.Log*` / `logger.Log*` through the
injected params. The support structs `MakerRollback` (L2345) and `InnocentBuyMakerRollback` (L2360) are pure
data holders used only here.

**Extraction:** move `RollbackRejectedFillsCore` + `MakerRollback` + `InnocentBuyMakerRollback` (and, by the
same argument, the already-static `RollbackMatch` at L2596) into a `static class RejectedFillRollback` in
`RejectedFillRollback.cs`. Keep the instance `RollbackRejectedFills` wrapper on OES (it is the single
production call binding `_accounts`/`_ledger`/`_logger`/`DebugUserId`), delegating to
`RejectedFillRollback.Apply(...)`. Preserve `internal` visibility so `InternalsVisibleTo` stays satisfied.

**Verdict: RejectedFillRollback is cleanly static — zero instance coupling.** The only caveat is the missing
self-test (§1): the move's oracle is the equivalence/conservation soaks unless the owner writes the cited-but-
absent `RollbackRejectedFillsSelfTest` first (cheap, and it would make this the lowest-risk of all three
extractions).

---

## 5. CK / conservation touch-points (reserve / settle / release + multi-table writes)

**8 transaction roots owned directly by OES** (7× `BeginTransactionAsync` + 1× `RunInTransactionAsync`), plus
~13 reserve/release drivers and the delegated `SettlementEngine` txs. Full enumeration:

| # | Site | Lines | What it drives | Fragment? |
|---|---|---|---|---|
| 1 | `PlaceAndMatchAsync` | 157–170 | `SettleOrderAsync` (reserve+persist) → `MatchAndSettleAsync` | — |
| 2 | `MatchAndSettleAsync` | 178–313 | book lock → Match → `SettleTradesAsync` → drift auto-cancel `UpdateAllAsync` → `RollbackRejectedFills`+`UpdateAllAsync` → `CancelRemainderAsync` | **do-not-fragment** (settle↔rollback↔remainder under one lock) |
| 3 | `PlaceBracketAsync` | 320–356 | `SettleOrderAsync` + per-child `CreateOrder` + `MatchAndSettleAsync` | — |
| 4 | `ArmStopAsync` | 362–379 | `SettleOrderAsync` (reserve+insert, no match) | — |
| 5 | `PromoteStopAsync` | 384–420 | `OnStopFiringAsync` + `UpdateOrder` (flip) + `MatchAndSettleAsync` (reuses arm reservation) | — |
| 6 | `CancelOrderAsync` | 422–452 | book lock → `CancelRemainderAsync` (release) + bracket teardown | — |
| 7 | `ModifyOrderAsync` | 454–579 | book lock → `ApplyOrderChangeAsync` (reservation delta) → Match → `SettleTradesAsync` → rollback paths | **do-not-fragment** |
| 8 | `ModifyStopAsync` | 584–604 | `ApplyStopChangeAsync` (off-book reservation delta) | — |
| 9 | Batch **Phase 1.5** | 649–736 | ungated in-cache share reserve (`ReserveStock`+`TakeSellReservation`) | pairs with #12/#14 restore |
| 10 | Batch **Phase 1.6** | 754–834 | ungated in-cache fund reserve (`ReserveFunds`+`TakeBuyReservation`) | pairs with #12/#14 restore |
| 11 | Batch **Phase 2** tx | 867–895 | `BeginTransactionAsync` → `InsertAllAsync` → commit; fail → `RestoreCacheSnapshots` | atomic insert |
| 12 | **`RunGroupTxAsync`** groupTx | 1092–1163 | **THE unit** — book→gates→tx→settle→commit; catch → rollback+`RollbackMatch`+`RestoreCacheSnapshots` | **DO-NOT-FRAGMENT (§2)** |
| 13 | **`RunCurrencyShardAsync`** | 1306–1316 | `RunInTransactionAsync` root — N groups as savepoints, one fsync | **DO-NOT-FRAGMENT (§2)** |
| 14 | `RecoverFailedGroupAsync` | 1383–1465 | own recovery tx: cancel + `UnreserveStock`/`UnreserveFunds` + `UpdateAllAsync` (release Phase 1.5/1.6) | atomic recovery |
| 15 | `CancelOrdersBatchAsync` tx | 1601–1646 | book→gates→re-read→tx→`ReleaseReservationInline`+`UpdateAllAsync`(orders/funds/pos)→commit | **do-not-fragment** |
| 16 | `ArmStopBatchAsync` armTx | 1812–1824 | `InsertAll` + `UpdateAll`(touchedPositions) in one tx | atomic |
| 17 | `ArmStopBuyBatchAsync` | 1912–2020 | GATED reserve loop (`AcquireUserGatesAsync`) → armTx `InsertAll`+`UpdateAll`(funds) | gate↔reserve pair |
| 18 | `PlaceMarketShortBatchAsync` | 2212–2257 | Phase 2 insert tx + group runners (short collateral reserved at FILL inside group tx) | via #12/#13 |
| 19 | `ReleaseReservationInline` | 2624–2684 | Fund/Position release; runs INSIDE #15's gate+tx | must stay under caller's gate |
| 20 | `RollbackRejectedFillsCore` §5a | 2518–2541 | `Position.UnreserveStock` release; runs inside the settle tx/savepoint | must stay under settle tx |
| 21 | `ApplyGroupPostCommit` | 1200–1246 | `TrackNewPosition`/notify/results/registry — durability-gated | **DO-NOT-FRAGMENT from #12/#13** |

**The do-NOT-fragment seams (summary):**
- **#12 tx-body ↔ #21 post-commit apply** — the durability gate (§2). Primary seam.
- **book → gates → tx lock order** (#12, #15, #17) — never split; splitting risks AB/BA deadlock or the P2
  race window.
- **reserve (#9/#10) ↔ restore/recover (#11/#12/#14)** — a Phase 1.5/1.6 reservation and its rollback are a
  pair; the reserve is ungated in-cache, so only `RestoreCacheSnapshots`/`RecoverFailedGroupAsync` release it.
- **settle ↔ RollbackRejectedFills ↔ cancelled-maker UpdateAll** (#2, #7, #12) — the rejected-fill cancel must
  live in the SAME tx/apply-pass as the settle it corrects.

---

## 6. Partial-carve proposal (byte-identical)

`public sealed partial class OrderExecutionService` (add `partial`; globbed csproj needs no edit). Concern
groups + rough line counts (source ≈ 2712 LOC). Assumes the **GroupCommitCoordinator collaborator (§2) and the
`RejectedFillRollback` static class (§4) are extracted first**, which removes ~410 + ~230 LOC into their own
files.

- **`OrderExecutionService.cs`** (spine, ~130) — 13 service fields, gate config, ctor, `GateFor`,
  `CollectAffectedUsers`, `FireBracketHooksAsync`. Holds the `GroupCommitCoordinator` reference. Keeps
  `sealed partial`.
- **`OrderExecutionService.Single.cs`** (~450) — the single-order region: `PlaceAndMatchAsync`,
  `MatchAndSettleAsync`, `PlaceBracketAsync`, `ArmStopAsync`, `PromoteStopAsync`, `CancelOrderAsync`,
  `ModifyOrderAsync`, `ModifyStopAsync`. **(CK seam — #2/#7 settle↔rollback; keep whole.)**
- **`OrderExecutionService.Batch.cs`** (~640, after coordinator extraction) — `PlaceAndMatchBatchAsync`,
  `CancelOrdersBatchAsync`, `ArmStopBatchAsync`, `ArmStopBuyBatchAsync`, `PlaceBracketBatchAsync`,
  `PlaceMarketShortBatchAsync`, `ProbeF1SameUserBuyerSeller`. **(CK seam — #9/#10/#15/#17 reserve+gate; keep
  each method whole.)**
- **`GroupCommitCoordinator.cs`** (~410, **own collaborator class, not a partial**) — the §2 machinery.
- **`RejectedFillRollback.cs`** (~230, **own `static class`, not a partial**) — `RollbackRejectedFillsCore`,
  `RollbackMatch`, `MakerRollback`, `InnocentBuyMakerRollback`; instance `RollbackRejectedFills` wrapper +
  `BuildOrdersById`, `ReleaseReservationInline`, `TryParseDriftedUserId` stay on the spine/Batch partial as
  thin OES helpers.

If the owner prefers a **pure partial-only** first pass (no collaborator extraction yet), the same members
carve into `.Single.cs` / `.Batch.cs` / `.GroupCommit.cs` / `.Helpers.cs` partials — but the group-commit
concern then stays a partial rather than a real collaborator, which does not satisfy the backlog's
"real-extract GroupCommitCoordinator" ask. Recommend the collaborator route.

---

## 7. Recommended ATTENDED SEQUENCE (one extraction per CK soak)

Ordered lowest-risk-first so each soak validates one clean seam. **Every step is CK-critical** except where
noted — one multi-hour CK soak per step, no batching two extractions into one soak.

1. **(Optional pre-step, cheap) Write the missing `RollbackRejectedFillsSelfTest`.** Deterministic, no soak.
   Gives step 2 a real unit oracle. *Gate:* build + new test green.
2. **`RejectedFillRollback` static class extraction (§4).** Lowest risk — already static, zero instance
   coupling. *Gate:* build green → **moves-only sorted-line diff** (the static class gains exactly the removed
   lines; the instance wrapper's body is unchanged) → full suite → **mid (45m) CK soak**, conservation/`CK_`/
   auditor clean (rejected-fill rollback only fires under maker-drift, so the soak must include a
   drift-inducing arm). **Owner call:** confirm `internal` visibility preserved.
3. **`GroupCommitCoordinator` collaborator extraction (§2) — the hard one, do it with the owner watching.**
   Move the whole machinery set as ONE unit; expose a single `RunGroupsAsync` entry; never surface
   `ApplyGroupPostCommit` outside the durability gate. *Gate:* build green → **`GroupCommitEquivalenceTests`
   + all `*BatchEquivalenceTests` green (byte-identical flag off/on)** → **`GroupCommitCrashTests` green
   (durable-row reconcile)** → full suite → **long (2h) CK soak** with `Db:GroupCommit:Enabled` **both off and
   on** (two arms), conservation/`CK_`/auditor clean on both. **Owner call:** confirm the tx-body↔post-commit
   seam and the `deferPostCommit`/`DeferredGroup` gate are intact, and that `_groupGate`/per-currency gates
   moved with the coordinator (not left dangling on OES).
4. **`.Batch` partial carve (§3/§6), then `.Single` partial, spine last.** Mechanical once the collaborator is
   out. *Gate per partial:* build green → moves-only sorted-line diff (spine loses exactly the moved members;
   partial gains exactly them; only added token is `partial`) → full suite → **mid (45m) CK soak** for the
   `.Batch` partial (it still holds the reserve/gate seams #9/#10/#15/#17); `.Single` needs build+suite+diff +
   a short CK smoke.

**Standing gates for every step:** (a) diff is **moves-only** — no logic edits ride along; (b) `internal`/
`public` visibility preserved so `InternalsVisibleTo` tests compile untouched; (c) the **book → gates → tx**
lock order and the **tx-body ↔ post-commit-apply durability gate** are never split; (d) the deliberate
byte-identical DUPLICATION in the batch methods (`PlaceMarketShortBatchAsync`, the short/plain split) is
preserved verbatim — do not "DRY" it; (e) flag-off path stays byte-identical (the equivalence tests are the
proof).

---

### One-glance summary
- **2712 LOC**, `public sealed` (not partial), implements `IOrderExecutionService`; namespace
  `…MarketEngineServices`; server csproj globbed (no csproj edits); 16 test files are the oracle, led by the
  `GroupCommit*Equivalence` (byte-identical flag off/on) + `GroupCommitCrashTests` (durable reconcile) suites.
- **GroupCommitCoordinator CAN be extracted as one whole tx-unit** — `RunGroupTxAsync` (tx body) +
  `ApplyGroupPostCommit` (post-commit apply) are already coupled through the `deferPostCommit`/`DeferredGroup`
  durability gate; key risk = exposing the post-commit apply outside that gate → cache/UI ahead of DB and a
  crash-reconcile divergence.
- **RejectedFillRollback is cleanly static** — `RollbackRejectedFillsCore` already `internal static`, zero
  instance coupling (all collaborators are params); only caveat is the cited self-test doesn't actually exist.
- **21 CK/tx touch-points** — 8 OES-owned transaction roots (7 `BeginTransactionAsync` + 1
  `RunInTransactionAsync`) plus ~13 reserve/release drivers; the do-not-fragment seams are the group-commit
  tx-body↔apply gate, the book→gates→tx lock order, and each reserve↔restore pair.
