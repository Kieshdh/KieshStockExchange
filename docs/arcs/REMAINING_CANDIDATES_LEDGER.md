# Remaining Oversized-File Candidates — Decision Ledger

READ-ONLY triage sweep (no code touched). Classifies the remaining oversized files for the
byte-identical restructure. Verdicts: **AUTO-PARTIAL** (clean verbatim partial split worthwhile) /
**NO-SPLIT** (cohesive / comment-inflated / already-partitioned / low-ROI) /
**ATTENDED** (CK blast-radius or welded invariant needing body edits).

Enhanced-gate lens applied: all fields/initializers/ctors must fit the spine; a split leaving the
spine still over ~500 is low-ROI; watch StructLayout / CallerFilePath / order-sensitive reflection.

Cap target ≈ 500 physical LOC.

---

## 1. AiBotStateService — 646 LOC — `Server/Services/BackgroundServices/Helpers/AiBotStateService.cs`
**VERDICT: AUTO-PARTIAL**

`internal sealed class`; add `partial`. All state is a handful of readonly deps + config flags + a
few mutable log-throttle bookkeeping fields — all trivially fit one spine. It operates on an
`AiBotContext` passed in as a parameter and does **not** mutate money itself (Fund/Position
mutations happen in the engine's `TradeSettler`, per the line-266 comment). It is a sibling of, not
coupled into, the Attended `AiBotDecisionService` — no reference to it. It is CK-sensitive (drives
cancels that release reservations), so the executor must move bodies **verbatim** (a byte-identical
partial changes zero behaviour → nil blast radius).

Concern partition (4 clean regions → 2 files):
- **Spine (~290 LOC):** fields + ctor + `#region Load and Refresh` + `Daily and Online` +
  `Transaction and Cache`.
- **`.Pruning` partial (~355 LOC):** the whole `#region Pruning` — `PruneWorstOrdersAsync`,
  `CancelAgedStopsAsync`, `CancelPriorStandaloneStopsAsync`, `NoteArmedStopPlaced`, static
  `ShouldCountArm`/`AgeJitterFactor`/`BotLifetimeFactor`. Self-contained; only reads spine deps.

Both halves land under 500.

---

## 2. AiBotContext — 637 LOC — `Server/Services/BackgroundServices/Helpers/AiBotContext.cs`
**VERDICT: NO-SPLIT** (deeper Phase-0 overturned the tentative AUTO-PARTIAL below — the "borderline
comment-inflated" flag was correct). Only ~355 substantive LOC (44% is comment/blank/region);
the one cleanly-peelable region (`Financial Computations`) is ~40 real lines, every other region drags
a field-relocation cost, and the type owns RNG-draw-order determinism (`AiUserRngs`/`GetRandom`) —
so a split trades a real determinism-review burden for cosmetic size. Bias-to-NO-SPLIT applies. Left intact.

_Original tentative rationale (superseded):_ **AUTO-PARTIAL** (borderline — heavily comment-inflated)

A per-tick data container: 3 ctor params (`accounts` + two bool flags), and a large but cohesive
field block (~lines 33–187) that is mostly Concurrent/plain dictionaries with multi-line WHY
comments. Critically, **all fields fit one spine** (~130 LOC of state region; nowhere near 500), so
the gate passes. `ClearAll` touches every field and stays in the spine (fields are spine-resident →
fine). No StructLayout / reflection-ordering hazards; dictionaries are already Concurrent for the
parallel sweep. The movable regions are pure, RNG-labelled math keyed only on params + spine fields.

Concern partition (fields cannot move; pure-math regions can → 2 files):
- **Spine (~260 LOC):** ctor + `State` + per-tick caches + `Accessors` + `Refill throttle` +
  `Inertia stance` + `Reaction/persistence` + `Impact-decouple` + `ClearAll`/`Helpers`.
- **`.Computations` partial (~370 LOC):** `Personal sentiment`, `Financial Computations`, and
  `Perceived-price desync` regions — self-contained pure functions.

Note: a large share of the byte count is field documentation; if the cap is treated softly this is a
defensible NO-SPLIT (comment inflation). Kept AUTO-PARTIAL because the physical file is 637 and the
math regions are genuinely separable.

---

## 3a. PgDBService.Orders.cs — 637 LOC — `Server/Services/DataServices/PgDBService.Orders.cs`
**VERDICT: NO-SPLIT** (already an entity-partial; further split redundant / low-ROI)

Already one entity-partial of the `PgDBService` partial class. Holds Order ops + Transaction ops +
their batched hot-writes — cohesive Dapper CRUD, no instance fields here (only file-local `const`
column lists). A further peel of the Transaction region into its own partial is *mechanically*
byte-trivial (would leave Orders ≈430, a new Transactions ≈205), but it doubles the partial-file
count of the same class for pure mechanical CRUD that is already partitioned by data layer — exactly
the "already-partitioned / low-ROI" NO-SPLIT case. Only revisit if the ~500 cap is enforced hard;
then the Transactions peel (its `TransactionCols` const + Transaction region + `InsertTransactionsBatchAsync`)
is the clean seam.

## 3b. PgDBService.Portfolio.cs — 567 LOC — `Server/Services/DataServices/PgDBService.Portfolio.cs`
**VERDICT: NO-SPLIT** (already-partitioned; only ~13% over; three cohesive entities)

Same story, milder: Position + Fund + FundTransaction CRUD + batched writes, ~567 LOC. Splitting by
entity yields three thin files for uniform boilerplate. Low ROI, already partitioned by the existing
partial scheme.

---

## 4. Order.cs — 586 LOC — `Shared/Models/Trading/Order.cs`
**VERDICT: NO-SPLIT** (cohesive domain model; invariant- and CK-welded)

Confirmed. Fields carry validating setters (immutability guards on Id/UserId/StockId, price/slippage/
budget range checks) — the invariants the mandate explicitly preserves. `IsValid`/`IsValidPrice`/
`IsValidBuyBudget` are welded to those fields; the `Current*Reservation` mutators mirror
`Fund.ReservedBalance` / `Position.ReservedQuantity` in lock-step (CK-critical). The only vaguely
separable slab is the ~50 LOC of `*Display` string projections, but peeling display strings off a
Shared domain model is low value and needlessly fragments an invariant-dense type. Cohesive → keep whole.

---

## 5. ExcelSeedService — 559 LOC — `Server/Services/SeedServices/ExcelSeedService.cs`
**VERDICT: AUTO-PARTIAL**

`sealed class`; add `partial`. State is 5 readonly deps + ctor — fits one spine. The per-sheet seed
steps are independent methods using only those deps + a `DataSet` param. It writes funds/positions,
but at **seed time** inside `RunInTransactionAsync` (setup, not live-CK) → no runtime CK blast radius.
Only ~59 over cap, but `SeedAIProfilesAsync` alone is ~140 LOC, so the split is clean and meaningful.

Concern partition (→ 2 files):
- **Spine (~215 LOC):** fields + ctor + public orchestration API (`SeedAllAsync`, `SeedKindAsync`,
  `SeedAllFromEmbeddedAsync`, `IsDatabaseEmptyAsync`, `EmbeddedWorkbookPath`) + `#region ClosedXML reader`
  (`ReadAllSheets`, `RequireSheet`).
- **`.Steps` partial (~345 LOC):** the `#region Seed steps` — `SeedStocksAsync`, `SeedListingsAsync`
  (+`ReadUsdSeedPricesAsync`/`ReadListingsFromSheet`), `SeedUsersAsync`, `SeedAIProfilesAsync`,
  `SeedHoldingsAsync`.

Both halves land under 500.

---

### Roll-up
| File | LOC | Verdict |
|------|----:|---------|
| AiBotStateService | 646 | ✅ SHIPPED — AUTO-PARTIAL (spine + `.Pruning`) `612fcc4` |
| AiBotContext | 637 | NO-SPLIT (deeper Phase-0: ~355 real LOC, RNG-order-sensitive, one ~40-line peelable region) |
| PgDBService.Orders.cs | 637 | NO-SPLIT (already entity-partial) |
| PgDBService.Portfolio.cs | 567 | NO-SPLIT (already entity-partial) |
| Order.cs | 586 | NO-SPLIT (cohesive, invariant/CK-welded) |
| ExcelSeedService | 559 | ✅ SHIPPED — AUTO-PARTIAL (spine + `.Seeds`/`.AiProfiles`) `704776d` |
