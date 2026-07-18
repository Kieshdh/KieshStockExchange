# Arc: BracketCoordinator split — TRIAGE VERDICT: **ATTENDED (hard-stop)**

**Target:** `KieshStockExchange.Server/Services/MarketEngineServices/BracketCoordinator.cs` (1216 LOC,
`public sealed class BracketCoordinator : IBracketCoordinator`). Read-only triage 2026-07-18 (council-mandated,
because the §6 backlog flagged this arc "Auto→Attended if the release seam moves").

## VERDICT: ATTENDED — do NOT split unattended. Needs Kiesh present + a multi-hour CK soak.

## Why (the release invariant fragments across the long/short cut)
The council's AUTO bar = the reservation-release invariant must live WHOLLY within one `.Core` partial,
achievable by pure relocation of intact method bodies. That bar CANNOT be met:
- Reservation release is **inline-welded into both sides' handler bodies**, not confined to helpers:
  - long: `sl.ConsumeSellReservation(...)` inline in `OnChildFillSyncAsync` (~L461) and `OnStopFiringSyncAsync` (~L556)
  - short: `fund.UnreserveFunds/ConsumeBuyReservation` inline in `OnChildFillShortAsync` (~L894) and `OnStopFiringShortAsync` (~L994)
- The four `*SyncAsync` dispatchers each mix **long body + short dispatch + inline long release in one method body**,
  so a whole dispatcher can't be assigned to a Long-only or Short-only file without straddling release logic.
- Therefore a `.Long`/`.Short` partition fragments the release invariant across at least the `.Long` and `.Short`
  files. Confining it to `.Core` would require **extracting the inline release blocks into new helpers = body edits /
  invariant-boundary restructuring** — the explicit ATTENDED trigger.
- No unit test pins the method-level release behaviour (BracketBatchEquivalenceTests MOCKS IBracketCoordinator;
  correctness is validated indirectly by settlement-level conservation tests + soak). So the real oracle is a
  multi-hour CK/conservation soak → owner-present territory.

## What IS clean (for when Kiesh runs it attended)
- Field/state layer cuts cleanly: all instance fields, no static, no initializer depends on another → all land in
  a `.Core` spine with zero init-order change. Nested types (`CoordinatorEventKind`, `CoordinatorEvent`, `LegState`)
  → `.Core`. No regions/partial-methods complicate the cut.
- The four `*ShortAsync` + `ArmShortTpsOwnCashAsync` are public-but-not-on-interface, zero external callers →
  partial-of-same-type calls resolve fine.
- Seams the owner must look at (where "confine release to Core" forces extraction): `OnChildFillSyncAsync`
  (~L455-498), `OnStopFiringSyncAsync` (~L555-581), `OnChildFillShortAsync` (~L878-902), `OnStopFiringShortAsync`
  (~L985-999).

## Suggested attended approach (owner decision)
Per overview §4-B: do the byte-identical structural split FIRST where clean (fields + non-release members into
`.Long`/`.Short`/`.Core`), then a SEPARATE attended arc that extracts the inline release blocks into named `.Core`
release helpers under a multi-hour CK=0 soak — one extraction per soak.
