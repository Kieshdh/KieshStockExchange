# P4 Bracket — Reservation & OCO Settlement Design (for Ultraplan)

## What this note is

P4 (bracket orders) is specified in `ADVANCED_ORDERS_PLAN.md` (the "Patch 4" section). Most of it is
mechanical and will be implemented straight from that plan:
- model `Order.ParentOrderId int?` + a dormant `Attached` status,
- migration `BracketOrders`,
- a `BracketCoordinator` that reacts to fills and arms/resizes/cancels the child legs,
- the `PlaceBracketRequest` DTO + controller endpoint,
- the `PlaceOrderView` ScrollView + up-to-3 TP rows UI.

**This note is only about the one conservation-critical piece the plan under-specifies:** how the
stop-loss leg and the (up to 3) take-profit legs **share the held shares' reservation**, and how a
bracket take-profit *fill* settles without breaking share/cash conservation. The owner chose to
harden this with Ultraplan before implementation, the same way P1/P2 were de-risked by tracing
settlement. **Please pressure-test the design below and return a hardened version + a risk register
(the failure modes), like the P1/P2 register in `ADVANCED_ORDERS_PLAN.md`.**

Locked product decisions (from the plan, do not relitigate): full bracket = **1 SL + up to 3
scale-out TP limits, group-OCO**; **A1** every leg tracks the parent fill; **allocation** = per-TP
qty, default equal, TPs need not cover 100% (SL covers the full remaining); **B1** a TP fill reduces
the SL, an SL fire cancels all open TPs; **SL ratchet deferred to P5**; cancel-the-remainder when a
leg fires while a limit parent still rests; cancel-unfilled-parent discards dormant children,
cancel-partial-parent keeps armed children.

## The crux: the SL must cover the full held qty, so legs can't each self-reserve

For a long bracket holding 10 shares with TP1=3 / TP2=3 / TP3=2 + an SL:
- **"Remainder" model** (TPs reserve 3+3+2, SL reserves the leftover 2) sums to 10 with no overlap and
  drops onto the existing reservation rules — **but it's wrong**: on a downside crash the SL only
  sells 2; the other 8 sit in TP limits *above* the market that never fill, so 8 shares are
  unprotected. Rejected.
- **"Shared-pool" model** (required, and what the plan means by "the SL holds one reservation, each TP
  draws a slice"): the **SL reserves the full held qty (10)**; the TP limits rest on the book
  reserving **nothing**, drawing from the SL's pool; a TP fill of `q` shrinks the SL to `10−q`; an SL
  fire cancels all TPs and sells the full held qty. Correct, and matches IBKR/Alpaca/TT (research in
  the plan: "TP partially filled for 3 → the OCO stop is discounted by 3").

The shared-pool model means **a resting bracket-TP limit sell with zero share-reservation must be
allowed to fill**, because its sibling SL holds the reservation. That is the cross-order handoff that
touches conservation-critical code.

## Current engine facts (verified 2026-06-04, cite these)

- **Lock order** is sacrosanct: book → per-user gates → DB tx. `TradeSettler.SettleAsync`
  (`Settlement/TradeSettler.cs:33`) acquires sorted user gates then a tx; the batch path gates inside
  `OrderExecutionService.RunGroupTxAsync`.
- **Share reservation lives on the Position** (`Position.ReservedQuantity`), mirrored per-order by
  `Order.CurrentSellReservedQty` — kept in lock-step at every reservation site under the user gate.
  `AvailableQuantity = Quantity − ReservedQuantity`.
- **`SellerCapacityValidator.Filter`** (`Settlement/SellerCapacityValidator.cs`) accepts a sell fill
  only when `order.CurrentSellReservedQty + Position.AvailableQuantity ≥ fill qty`. It already has a
  **P1 short-opening branch** that accepts a flat seller's market sell as capacity-valid (cash
  collateral, not shares, is the backing) — the precedent for adding a **bracket-TP branch**.
- **`TradeSettler.SettleNoTxAsync` long-sell path** (`:394-436`): if `Position.ReservedQuantity <
  fill qty` it tops up from `AvailableQuantity` (`TakerSellTopUp`) then `Position.ConsumeReservedStock(qty)`
  (drops `ReservedQuantity` and `Quantity`), and `sellOrder.ConsumeSellReservation(min(qty, its
  CurrentSellReservedQty))`. A bracket-TP has `CurrentSellReservedQty = 0`, so the order-level consume
  is a no-op — **but the Position's `ReservedQuantity` (held by the SL) is what actually drops.**
- **`ConservationProbe.Check`** (`:502`) asserts per-ccy/stock fund+share deltas net to 0 across the
  batch. A normal buyer↔seller fill already nets; the bracket-TP fill is an ordinary sale of held
  shares, so the probe should stay green **iff** the share leaving the seller's position is matched by
  the buyer's credit (it is) — the only open question is the per-order reservation bookkeeping, not the
  probe.
- **`ReservationAuditor` / `ReconcileReservationsAsync`** reconciles `Position.ReservedQuantity`
  against the sum of open sell orders' reservations (and `Fund.ReservedBalance` against Σ open-buy
  reservations + Σ `Position.ShortCollateral`). **This is the component most likely to flag the
  bracket — please confirm exactly what it sums and how it heals a mismatch (cancel? clamp?).**
- **`AccountsCache` backfill** rebuilds `ReservedQuantity`/`ReservedBalance` from open orders on
  restart (+ short collateral from positions). A bracket's shared reservation must rehydrate to the
  SL leg, not be double-counted across the TP legs.
- **The SL leg is an armed stop** (`Status=Pending`, off-book) promoted by `StopTriggerWatcher`
  (`HostedServices/StopTriggerWatcher.cs`) via `OrderExecutionService.PromoteStopAsync`. P2 reserves a
  sell-stop's shares **at arm time**.
- **Arming happens after the parent fills**, so the held shares already exist when the SL reserves
  them — no place-time double-reserve.

## Proposed design (Model B) — please harden

**Identity.** A child carries `ParentOrderId`. Within a bracket: the **SL** child is the one with
`Stop=Stop`; the **TP** children are `Entry=Limit, Stop=None`. All start `Status=Attached` (dormant:
reserve nothing, not on book, not in watcher).

**Arming on parent fill (A1).** When the parent's cumulative `AmountFilled` increases (coordinator,
post-commit, under the owner's gate):
- `held = parent.AmountFilled − Σ(child.AmountFilled)` (the still-open bracket position).
- **SL** is armed/resized to `held` and reserves `held` shares on the Position (the single shared
  pool), exactly the P2 arm path. `SL.CurrentSellReservedQty = held`, `Position.ReservedQuantity +=`
  the delta.
- Each **TP_i** is armed onto the book as a resting limit sell sized to its slice of the fill
  (pro-rata of `allocated_i`, integer-rounded so Σ open TP ≤ held), reserving **nothing**
  (`CurrentSellReservedQty = 0`). It is flagged a bracket-TP (via `ParentOrderId` + `Entry=Limit`).

**Bracket-TP fill (the handoff).** When a resting bracket-TP limit fills `q` (it's a maker in some
incoming order's match):
1. `SellerCapacityValidator` — new branch: a bracket-TP sell is capacity-valid because its bracket's
   shares are reserved on the Position by the SL (mirror the P1 short-acceptance branch; detect via
   `order.ParentOrderId is not null && order.IsLimitOrder && order.IsSellOrder`).
2. `TradeSettler` — the existing long-sell path already does `Position.ConsumeReservedStock(q)` (drops
   the SL-held reservation) + `Position.Quantity −= q`. The TP's own `ConsumeSellReservation` is a
   no-op (0). **The sibling SL's `CurrentSellReservedQty` must drop by `q`** to stay in lock-step with
   `Position.ReservedQuantity`. **Open fork:** do this decrement (a) inline in `TradeSettler` (needs
   the sibling SL loaded mid-settlement — it isn't in `ordersById`), or (b) post-commit in the
   coordinator's reconcile (`SL.qty = held−q`, `SL.CurrentSellReservedQty −= q`), accepting a transient
   window where Σ order reservations (SL still = old held) > `Position.ReservedQuantity`. **Which is
   safe w.r.t. `ReservationAuditor` running in that window?**
3. The coordinator then resizes/cancels per **B1**: SL shrinks to the new `held`; if `held == 0`
   (everything sold via TPs) cancel the SL; cancel-the-remainder of a limit parent if appropriate.

**SL fire (OCO).** When the SL triggers, the bracket must sell the **full held** qty, not the
remainder — so the coordinator must, atomically under the gate, **cancel all open TP legs first**
(they reserve nothing, so cancel is a book-remove with no reservation release), confirm the SL is
sized to `held`, then let `PromoteStopAsync` match it. **Open fork:** the plain `StopTriggerWatcher`
promote path doesn't know to cancel siblings — does the SL promotion route through the coordinator
(bracket-aware promote) instead, or does `PromoteStopAsync` gain a "cancel bracket siblings" step?

**Short bracket** (sell/short entry → buy-to-cover SL above + buy TPs below): the analog uses **cash
collateral** (the P1 short model) instead of share reservation — the SL is a buy-stop holding the
buy-side reservation, TPs are buy limits drawing on it. Please confirm the cash-side handoff mirrors
the share-side and keeps `Fund.ReservedBalance` reconciled.

## Invariants the hardened design must hold
1. At rest, **Σ(reservation across all bracket legs) == the held position** (no over- or
   under-reservation) — so `Position.ReservedQuantity` and `Fund.ReservedBalance` reconcile.
2. `ConservationProbe` stays green across every bracket fill (TP fill, SL fire, partial parent fill).
3. `ReservationAuditor` does not flag or auto-cancel a healthy bracket — including across the
   post-commit reconcile window and across a restart (`AccountsCache` hydration).
4. The held shares are **always fully protected**: at any instant the SL covers `held`, regardless of
   how many TP legs are open or partially filled.
5. Lock order book → gates → tx is never violated; the coordinator re-acquires gates, never holds the
   book.

## Questions for Ultraplan (the forks above, collected)
1. **Reservation handoff site:** inline in `TradeSettler` (load sibling SL mid-settlement) vs.
   post-commit coordinator reconcile (transient mismatch). Which keeps `ReservationAuditor` correct?
2. **SL-fire sibling cancel:** bracket-aware promote in the coordinator vs. a hook in `PromoteStopAsync`.
3. **`ReservationAuditor` exact behavior** on a bracket (what it sums; how it heals) — does the
   shared-pool model need an auditor change like P1 needed for short collateral?
4. **TP allocation as the parent fills:** pro-rata per fill vs. fill-up TP1→TP2→TP3; integer rounding rule.
5. **Coordinator vs. in-tx:** is reacting to fills post-commit (re-acquiring the gate) acceptable, or
   must arming/resizing be inside the settling tx for atomicity?
6. **Bracket-TP detection in the hot path** (`SellerCapacityValidator`/`TradeSettler`): is
   `ParentOrderId is not null` cheap+sufficient, or do we need an explicit leg-role flag/column?
