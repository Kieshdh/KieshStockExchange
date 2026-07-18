# Arc: TradeSettler split — PREPARE-BUT-HOLD (owner-eyes / ATTENDED)

**Target:** `KieshStockExchange.Server/Services/MarketEngineServices/Settlement/TradeSettler.cs`
Read-only analysis 2026-07-18 (council-mandated hold). Fable-5 ruled this **owner-eyes territory even for a
byte-identical move** because it is the settlement core (reserve→settle→release) and its only real oracle is a
multi-hour CK/conservation soak. This document STAGES the split for Kiesh to run attended; it does NOT authorize
an unattended execution.

## VERDICT (one line)
**FEASIBLE-AS-PURE-PARTIAL — but LOW-VALUE.** A byte-identical partial split is mechanically clean (whole intact
methods relocate, no body edits), yet it only sheds ~110 lines of separable concerns; the 655-line settlement
monolith `SettleNoTxAsync` is intra-method inline-welded and MUST stay whole in the spine. The meaningful shrink
(extracting the apply-pass phases) is BODY EDITS = a strictly bigger, separate attended arc.

---

## 1. File identity

| Fact | Value |
|---|---|
| Path | `KieshStockExchange.Server/Services/MarketEngineServices/Settlement/TradeSettler.cs` |
| Exact LOC | **890** (`wc -l`; 890 code+comment+blank lines, closing `}` on line 890) |
| Comment lines | 170 (incl. XML doc + terse phase markers) → ~19% of file |
| Blank lines | 58 → ~6.5% |
| Effective code | ~662 lines |
| Namespace | `KieshStockExchange.Services.MarketEngineServices` — **NOTE: parent of the folder.** The file lives in `.../Settlement/` but the namespace is the `MarketEngineServices` root, NOT `...Settlement`. Any partial file MUST repeat this exact namespace or the `partial` won't unify. |
| Declaration | `internal sealed class TradeSettler` — **not** `partial` today, no base class, no interface. |
| csproj auto-include | `KieshStockExchange.Server.csproj` is `Microsoft.NET.Sdk.Web`, `EnableDefaultCompileItems` NOT disabled → all `.cs` under the project auto-compile. New `TradeSettler.*.cs` partial files under `Settlement/` compile with zero csproj edit. No explicit `<Compile>` items exist. |
| InternalsVisibleTo | `KieshStockExchange.Tests` and `DynamicProxyGenAssembly2` (Moq), both in the Server csproj (lines 43/45). TradeSettler is `internal` → tests never `new TradeSettler(...)` directly; they reach it through the internal `SettlementEngine`, which owns `new TradeSettler(...)` at `SettlementEngine.cs:44`. |

### The oracle tests (what actually exercises it)
No test constructs `TradeSettler` directly. Every settlement test builds a `SettlementEngine`
(`new SettlementEngine(db, accounts, ledger, registry, …)`) and drives `SettleTradesAsync`, which delegates to
`TradeSettler.SettleAsync` / `SettleNoTxAsync`. The behavioural oracle set:

- **`ShareConservationTests`** — the conservation-probe / CK=0 backbone (line 88 builds the engine).
- **`GroupCommitEquivalenceTests`, `GroupCommitCrashTests`, `GroupCommitSharedPositionEquivalenceTests`,
  `GroupCommitSharedPositionFillTests`, `GroupCommitFsyncMicrobenchTests`** — batch-commit / shared-Position race
  behaviour through the settler.
- **`BracketBatchEquivalenceTests`, `BracketMixedPortionSettlementTests`, `BracketSettlementTests`,
  `BracketReconcileTests`** — bracket TP/SL settlement acceptance + the §P6 sibling-SL-pool draw path.
- **`FlipSettlementTests`, `FlipBatchInterleavingTests`, `BracketFlipDeterminismTests`** — the long→short flip
  branch and the Q7 pre-write CK defense (explicitly cite `TradeSettler.cs:527-549`).
- **`MarketShortBatchEquivalenceTests`, `MarketShortBatchFillEquivalenceTests`** — `isShortFill` collateral-at-fill.
- **`ShortPositionModelTests`** — the Position-side short/collateral invariants the settler must preserve.
- **`MatcherStatusRollbackTests`** — `RestoreSnapshots` Order.Status replay (mirrors the loop at `TradeSettler.cs:884-888`).
- **`ArmStopBatchEquivalenceTests`, `ArmStopBuyBatchEquivalenceTests`, `PerCurrencyGroupGateEquivalenceTests`,
  `ArbBatchLegsEquivalenceTests`, `SharedScanEquivalenceTests`** — batch/gate/arb equivalence through the same settle path.

Several of these tests hard-code `TradeSettler.cs` **line numbers** in comments (e.g. `BracketMixedPortionSettlementTests`
cites `:816-824` and `:783-858`; `FlipBatchInterleavingTests` cites `:527-549`, `:152`; `MatcherStatusRollbackTests`
cites `:854-858`). These are comments only (won't break the build) but a move WILL invalidate them — flag for a
comment-refresh pass, do not silently leave them wrong.

---

## 2. THE CK INVARIANT (reserve → settle → release)

The invariant — *fund + share deltas sum to 0 per (ccy, stock), and every reserved unit is accounted for* — is
realised almost entirely inside the single apply-pass loop of **`SettleNoTxAsync`** (lines ~180–650), then
verified and persisted (lines 714–769). The reserve/settle/release touch-points, in order:

1. **Buyer settle** (L213–262): `ConsumeReservedFunds(reservedPortion)` from `ReservedBalance`, `DrawSiblingSlPool`
   for a short-bracket-TP buyback, `WithdrawFunds(excess)` from available, and the lock-step
   `buyOrder.ConsumeBuyReservation(consume)` so the order field tracks the fund aggregate.
2. **Savings release** (L263–286): `UnreserveFunds(savings)` when a limit/slippage buy over-reserved, again
   lock-stepped to `ConsumeBuyReservation`.
3. **Seller settle** (L300–317): `sellerFund.TotalBalance += notional`.
4. **Buyer position settle** (L338–343): `buyerPos.Quantity += t.Quantity`.
5. **Short-close release** (L345–400): decoupled Position release (`ReleaseShortCollateral`, authoritative) vs
   clamped Fund `UnreserveFunds` — the tiny-shortfall-to-reconciler seam.
6. **Short-open reserve** (L458–495): `ShortCollateralForFill` reserved via order hold
   (`ConsumeShortCollateral`) or `ReserveFunds`, posted to `Position.TakeShortCollateral`.
7. **Flip reserve/settle** (L496–601): long part `ConsumeReservedStock` + `ConsumeSellReservation` FIRST, then
   short part with the post-`ApplyDelta` live-Quantity re-check (Q7 fix).
8. **Long-sell reserve/settle** (L602–649): taker top-up `ReserveStock`/`TakeSellReservation` then
   `ConsumeReservedStock`/`ConsumeSellReservation`.
9. **TrueMarketBuy leftover release** (L675–712): `UnreserveFunds` + `ReleaseBuyReservation`.
10. **CK checkpoints**: `ConservationProbe.Check(...)` (L715) and the Q7 pre-write / pre-insert
    `FindInvariantViolation` gates (L732–769).
11. **Rollback release**: `RestoreSnapshots` (L805–889) replays Fund/Position/collateral/order-reservation/
    Order.Status snapshots on any failure.

**Shared mutable state these depend on:** the 7 injected fields (`_db, _accounts, _ledger, _logger, _validator,
_probe, _registry`), and — critically — the per-call locals `fundMap`, `posMap`, `pendingNewPositions`,
`newPositionsThisCall`, plus the `TradeBatchScope` snapshot dicts and the **local function `SnapshotOrderIfNew`**
(a closure over `scope`). Every reserve/settle/release step reads and mutates these same locals within one loop
iteration.

**Why it must stay coherent in one type (§6 rule):** the reserve→settle→release chain per fill is a single
atomic accounting identity. Splitting the apply-pass across methods that don't share the loop's locals would
either (a) require passing a fat mutable context object between them (body edits + new surface) or (b) risk a
future edit landing a reserve in one file and its matching release in another, drifting CK off 0. Keeping it in
one type (which partials satisfy) — and, in practice, one *method* — keeps the identity auditable in a single
read. The §6 bar ("reserve→settle→release CK invariant stays visible in one type") is met **trivially** here
because it is not merely in one type, it is in one method.

---

## 3. Pure byte-identical partial split — feasible?

**Decisive: FEASIBLE-AS-PURE-PARTIAL (for the relocatable members), NEEDS-BODY-EDITS (for any real shrink of the core).**

Contrast with BracketCoordinator (which was ruled NEEDS-BODY-EDITS): there the release invariant was **welded
across multiple members** (four `*SyncAsync` dispatchers each carry inline release), so no `.Long`/`.Short`
partition could avoid straddling it. TradeSettler is the *opposite shape*: the invariant is welded **within one
member** (`SettleNoTxAsync`), not across members. That means:

- The apply-pass monolith cannot be partitioned **internally** — a C# method body cannot span partial files, and
  its branches share loop locals + the `SnapshotOrderIfNew` closure. Any attempt to break it up = extract helpers
  = **body edits**.
- BUT the *other* members are whole, self-contained, and relocate byte-for-byte:
  - `RestoreSnapshots` (L805–889, ~85 lines) — pure rollback replay; touches only `_accounts`, `_ledger`, and
    `scope`. No caller inside the apply-pass loop.
  - `FindInvariantViolation` (L779–802, ~24 lines) — pure predicate over a Position list + `_logger`.
  - `DrawSiblingSlPool` (L40–56, ~17 lines) — self-contained helper; touches `_registry`, `_ledger`, `TimeHelper`.

None of these three *inline-welds* the invariant across a member boundary: they are called from within
`SettleNoTxAsync` but are already separate methods with clean signatures. Relocating them to a `partial` file
changes zero IL — the only edit is the class-decl keyword (`internal sealed class` → `internal sealed partial
class`) in each file, which is a declaration change, not a body edit, and preserves member IL exactly.

**So:** a *pure partial split* is genuinely available, but it is **low-value** — the spine still holds the
655-line `SettleNoTxAsync`, so the file drops only ~110 lines. The valuable shrink (turning the apply-pass
branches into named phase helpers, per the repo's method-extraction-granularity feedback) is precisely the
BODY-EDIT job the council reserved for attended work.

---

## 4. Order-sensitivity check

- `[StructLayout]` — **none.** Not a struct; no explicit layout.
- `[CallerFilePath]` / `[CallerMemberName]` / `[CallerLineNumber]` — **none** in this file.
- Order-dependent reflection / serialization — **none.** No `[Serializable]`, no `JsonProperty`, no attribute
  ordering, no reflection over member order. `TradeSettler` is never serialized; it is a transient service.
- Field-initializer inter-dependence — **none.** All 7 fields are `readonly`, assigned only in the ctor from
  ctor args with null-guards; no field initializer references another field. Member/field textual order is
  therefore behaviourally irrelevant → a partial split cannot change semantics via ordering.

Conclusion: **no order-sensitivity blocks the split.**

---

## 5. Proposed member→file plan (IF the owner runs it as a pure partial)

Keep the reserve-settle-release core intact in the spine; move only the two cleanly-separable concerns. Recommend
**leaving `DrawSiblingSlPool` in the spine** (it is a direct participant in the buyer-settle reserve draw at
L234; keeping it beside its only caller maximizes §6 invariant visibility) — but it is *eligible* to move if the
owner prefers a thinner spine. Present both to Kiesh.

| File | Members | Target LOC |
|---|---|---|
| `TradeSettler.cs` (spine) | 7 fields + ctor + `SettleAsync` (tx wrapper) + `SettleNoTxAsync` (incl. local `SnapshotOrderIfNew`) + `DrawSiblingSlPool` | ~730 |
| `TradeSettler.Rollback.cs` | `RestoreSnapshots` | ~95 (incl. header) |
| `TradeSettler.Invariants.cs` | `FindInvariantViolation` | ~35 (incl. header) |

Each new file: same `using` block subset actually needed, exact namespace
`KieshStockExchange.Services.MarketEngineServices`, decl `internal sealed partial class TradeSettler`. Spine decl
also gains `partial`. Net spine reduction ~110 lines. (If `DrawSiblingSlPool` also moves → a
`TradeSettler.Reservations.cs` ~30 LOC and spine ~700.)

### EXACT gate the owner must run before merge
1. `dotnet build KieshStockExchange.Server/KieshStockExchange.Server.csproj` — compile-clean (0 warnings delta).
2. **Full suite ×3** (non-determinism guard): `dotnet test KieshStockExchange.Tests` three times, all green,
   focus on the oracle set in §1.
3. **Field/ctor spine-check**: confirm all 7 `readonly` fields + the ctor remain in `TradeSettler.cs` and are
   unchanged (grep the spine for the field block + ctor null-guards).
4. **Exact-usings check**: `git diff` shows no `using` added/removed beyond redistributing existing ones; no new
   namespace pulled in.
5. **Moves-only diff**: `git diff -M --stat` + a per-hunk read confirming every moved method body is
   byte-identical (only the class-decl `partial` keyword and file boundaries change). Zero token change inside
   any method body.
6. **45-min CK=0 soak** with ReservationAuditor clean: run the standard settlement soak, assert
   `ConservationProbe` never trips, `CK_Positions_Quantity_Invariants` count = 0, ReservationAuditor reconcile
   warnings within the benign `Bots:ReservationPhantomWarnThreshold` band (no growth vs a pre-split baseline soak).

---

## 6. Precise diff/seams the owner must eyeball before merging

Because the split is member-boundary-only, the seams are the three cut points plus the decl change — but the
owner's real attention should go to confirming the invariant did NOT fragment:

1. **`SettleNoTxAsync` stays whole in the spine** — verify the apply-pass loop (L180–650), the `SnapshotOrderIfNew`
   local function (L147–153), the ConservationProbe call (L715), and the persist/Q7 block (L717–769) are all in
   one file, one method, untouched. This is the §6 heart — it must not be carved.
2. **`RestoreSnapshots` cut (L805 boundary)** — confirm the whole rollback (Budget → Fund → Position →
   ShortCollateral → OrderReservation → Order.Status replay loops) moved as one intact body; its `_accounts` /
   `_ledger` / `scope` references resolve as same-type partial members.
3. **`FindInvariantViolation` cut (L779 boundary)** — confirm the predicate + the ERROR-log line moved intact and
   both call sites in the spine (L745, L759) still bind.
4. **`DrawSiblingSlPool` (L40)** — if left in spine (recommended), confirm it still sits beside its L234 caller;
   if moved, eyeball that the SL-pool draw's fund/`CurrentBuyReservation` lock-step logic is byte-identical.
5. **Decl change** — every partial file declares `internal sealed partial class TradeSettler` in the exact
   `MarketEngineServices` (not `.Settlement`) namespace; the spine gained only the `partial` keyword.
6. **Test comment drift** — the line-number references baked into the oracle tests (§1) now point at wrong lines;
   schedule a comment-refresh (non-blocking, but do it).

---

## Recommendation
Present to Kiesh as: *"Pure partial split is available and safe but only sheds ~110 lines; the 655-line
`SettleNoTxAsync` monolith is the real weight and can only shrink via a separate attended body-edit arc that
extracts apply-pass phases under a CK=0 soak, one extraction per soak."* Do the byte-identical relocation FIRST
(if worth it at all), the phase-extraction as a distinct attended round — never both unattended.
