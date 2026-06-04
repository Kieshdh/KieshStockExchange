# Long→Short Flip — Single-Order Split-Fill Design (for Ultraplan)

## What this is

Mirror how real brokers handle a sell that exceeds the holding: one order seamlessly **closes the
long and opens a short** for the excess (the position goes negative in a single order). Today the
engine rejects this "mixed close+open" — it's **risk #7** in `ADVANCED_ORDERS_PLAN.md`, deferred from
P1 precisely because it's a **conservation-critical split-fill** (one fill is part long-close,
part short-open, crossing zero). This note hardens that change the same way the bracket reservation
note did: traced against the real settlement code, with the forks and a risk register, for Ultraplan
to pressure-test before implementation. **Please return a hardened design + risk register.**

Scope: **MARKET sells only** (mirrors the P1 short MVP — a flat/short seller already shorts via market
sells; resting-limit flips stay out of scope). Already-shipped + working: flat→short open, and
short→extend (`OrderSettler` guard now `<=0`). The only missing case is a **long holder selling more
than they hold** (`0 < held < qty`).

## The crux

A market sell of `Q` by a seller holding `L` shares (`0 < L < Q`) must, in one order:
- **close the long**: sell `L` shares from inventory (share-reserved, `ConsumeReservedStock`), and
- **open a short**: sell `Q − L` shares the seller doesn't own (cash-collateralized, the P1 model).

These are two different reservation models straddling one order/fill. The fill that crosses zero is
itself part long-close, part short-open.

## Current code facts (verified 2026-06-04 — cite these)

- **Lock order** book → per-user gates → DB tx is sacrosanct.
- **`OrderSettler.SettleAsync`** (place time, `Settlement/OrderSettler.cs`): a sell is either
  `shortOpen = IsMarketOrder && (existing is null || existing.Quantity <= 0)` → reserves **no** shares
  (collateral posted at fill), or the long branch → `Position.ReserveStock(Q)` and **rejects when
  `AvailableQuantity < Q`**. For a flip (`L>0`, `Q>L`) it takes the long branch and rejects (only `L`
  available). **Needed:** reserve the long portion `min(L, Q) = L` shares + leave the `Q−L` short
  portion for fill-time collateral.
- **`SellerCapacityValidator.Filter`** (`Settlement/SellerCapacityValidator.cs`): a short branch
  accepts a fill when `IsMarketOrder && startQty <= 0` (no share draw); otherwise the long branch
  requires `order.CurrentSellReservedQty + Position.AvailableQuantity >= fill qty`. For a flip
  (`startQty = L > 0`) it's the long branch, which rejects the short portion. **Needed:** a mixed
  branch — draw the long portion from the order's reservation pool, accept the short portion as
  collateral-backed (like the existing short branch). (Beware double-counting vs the new bracket-TP
  per-position pool — different feature, same file.)
- **`TradeSettler.SettleNoTxAsync`** seller branch (`Settlement/TradeSettler.cs:~370`):
  `isShortFill = sellOrder.IsMarketOrder && sellerStartQty <= 0` → all-short (`ReserveFunds(collateral)`,
  `Position.ApplyDelta(-q)`, `TakeShortCollateral`). Else long → `ConsumeReservedStock(q)`. For a flip
  fill, `sellerStartQty = L > 0` → long path → `ConsumeReservedStock(q)` exceeds the `L` reserved →
  fails. **Needed:** per-fill **split at the running zero-crossing**: with `running` = position before
  this fill, `longPart = clamp(q, 0, max(0, running))`, `shortPart = q − longPart`;
  `ConsumeReservedStock(longPart)` (+ the order's sell-reservation consume) for the long part and the
  P1 short-open (`ReserveFunds`, `ApplyDelta(-shortPart)`, `TakeShortCollateral`) for the short part.
  `sellerStartQty` is the pre-batch snapshot (`posSnapshots`); track the running position across this
  order's fills.
- **P1 short-open** in `TradeSettler` already does collateral = `ShortCollateralForFill(qty, fillPrice,
  ccy)` → `Fund.ReserveFunds` + `Position.ApplyDelta(-qty)` + `Position.TakeShortCollateral`. Reuse for
  the short portion.
- **`ConservationProbe.Check`** asserts per-ccy/stock fund+share deltas net 0. The flip fill: seller's
  signed `Position.Quantity` delta is `−q` (long-close `−longPart` + short-open `−shortPart`); buyer
  `+q`; cash buyer↔seller nets; collateral is a `ReservedBalance` lock (invisible to the probe). Should
  stay green — **confirm the combined long+short mutation reports a single `−q` to the probe.**
- **Reconciler/clamp + `AccountsCache` backfill**: after the flip the seller is short with collateral
  (the P1 risk #8 fix already folds `Σ ShortCollateral` into the expected fund reservation). Confirm a
  flip leaves no phantom (the consumed long reservation + the new short collateral both reconcile).
- **`OrderValidator`** is structural (no holdings) — confirm it does **not** gate `Q > AvailableQuantity`
  for a market sell (the holdings decision lives in `OrderSettler`); the P1 "short-opening sell requires
  `AvailableQuantity == 0`" rule, **if** present anywhere, must be relaxed to allow the flip.
- **Rollback**: a flip fill mutates share reservation **and** collateral. The `TradeBatchScope`
  snapshots (`PosSnapshots`, `PosShortCollateralSnapshots`, `FundSnapshots`, `OrderReservationSnapshots`)
  must all capture the pre-flip state so `RestoreSnapshots` undoes both sides.

## Proposed design (per-fill split — please harden)

1. **Place (`OrderSettler`):** for a market sell with `0 < L < Q`, reserve `L` shares
   (`Position.ReserveStock(L)`, `order.TakeSellReservation(L)`); the `Q−L` short portion reserves
   nothing now (collateral at fill). Detect via `_accounts` (it has the position). The order's
   `CurrentSellReservedQty = L` then naturally tells the validator/settler how much is long.
2. **Validate (`SellerCapacityValidator`):** mixed branch — `fromReserved = min(reservedThis, q)` long
   (draw down the pool), the remainder accepted as short (collateral-backed, no share draw). Decrement
   the order's reserved pool per fill so it can't over-draw the long side.
3. **Settle (`TradeSettler`):** per fill, `longPart = min(q, max(0, running))`, `shortPart = q −
   longPart`. Long part: `ConsumeReservedStock(longPart)` + `order.ConsumeSellReservation(longPart)`.
   Short part: `ReserveFunds(collateral(shortPart, fillPrice))` + `Position.ApplyDelta(-shortPart)` +
   `TakeShortCollateral`. (For a pure-long fill `shortPart=0`; pure-short `longPart=0` — same code path
   collapses to today's two branches, so it's a generalization, not a rewrite.)
4. **UI:** the place ticket already warns ("To short, sell to flat first" → change to "Closes your long
   and opens a short for the rest"); a confirm popup is the separate planning item.

## Forks for Ultraplan
1. **Reservation handoff for the short portion in the validator** — inline (decrement a per-order pool)
   vs. relying on `TradeSettler` to top-up. Which keeps reconcile correct?
2. **Running-position source in `TradeSettler`** — pre-batch snapshot + running counter vs. live
   `Position.Quantity` (already mutated mid-batch). Avoid the same snapshot pitfall as P1.
3. **Probe**: confirm a single combined `−q` signed delta (not two separate mutations) so
   `ConservationProbe.Check` sees a clean net.
4. **`OrderValidator`**: is there any `AvailableQuantity == 0` / mixed-reject rule to relax?
5. **Rollback completeness** across the four scope snapshot maps for a half-long/half-short fill.
6. **Interaction with brackets**: a bracket parent is a buy; its TP/SL legs are sells — could a bracket
   SL ever flip? (No — it sells only the held position.) Confirm no cross-talk with the bracket-TP
   per-position pool in `SellerCapacityValidator`.

## Invariants (assert in a ConservationProbe/ReservationAuditor test)
1. After a flip, `Position.Quantity == L − Q` (negative), `ShortCollateral == (Q−L)×fill`, share
   reservation fully consumed, fund reserved == collateral.
2. `ConservationProbe` green across the crossing fill (and across multi-fill flips).
3. `ReservationAuditor` (clamp=true) flags nothing after a flip (steady state + restart).
4. Lock order preserved; rollback restores both share + collateral state.
