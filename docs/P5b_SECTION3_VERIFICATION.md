# P5b §3 — verification against the live collateral code (for Ultraplan, before the mechanical mirror)

I sanity-checked Ultraplan's §3 design against the current code (HEAD `d888716`, which Ultraplan couldn't
read). **Verdict: §3's reservation model is sound and composes with the live paths.** The disjoint-bucket
invariant `Σ(leg buy-reservation) + Σ(short collateral) == Fund.ReservedBalance` holds. Three confirmations,
two precisions, and **one real catch** to fold into the design before the mechanical mirror.

## Confirmed
1. **Collateral-release clamp (item 1).** `TradeSettler.cs:308–309`: `releaseColl = Min(releaseColl,
   Min(buyerPos.ShortCollateral, buyerFund.ReservedBalance))`. Bounds the release so it can never over-draw
   `ReservedBalance`. ✓ — the load-bearing safety net.
2. **Fund primitives (the conservation walk).** `Fund.ConsumeReservedFunds(a)` ⇒ `Reserved −= a; Total −= a`
   (payment leaves); `UnreserveFunds(a)` ⇒ `Reserved −= a` only (lock back to Available); `ReserveFunds(a)`
   ⇒ `Reserved += a`. All require `Reserved ≥ a` / `Available ≥ a`. Ultraplan's per-event walk-table
   reproduces exactly. ✓
3. **Arming point (item 3, code half).** Long arms the pool at **entry-fill** under the gate
   (`BracketCoordinator.OnParentFillAsync`); the short mirror arming at entry-fill is consistent. ✓
   (The funding-failure *policy* is a product decision — see "Needs a decision".)

## Precisions (Ultraplan's item 2 — accurate intent, looser mechanism)
- A closing buy does **not** fund its buyback from "its own `CurrentBuyReservation`." It funds at the
  **fund-aggregate** level: `buyerFund.ConsumeReservedFunds(notional)` (`TradeSettler.cs:183`), which reduces
  `Fund.ReservedBalance` regardless of which order owns it. The order's `CurrentBuyReservation` is consumed
  **separately and clamped**: `consume = Min(notional, buyOrder.CurrentBuyReservation)` (`:200`).
- **Why this still works for "SL owns the pool, TP reserves 0":** when a TP (reservation 0) fills, the
  `ConsumeReservedFunds(P·N)` automatically draws the buyback from the fund aggregate — which physically *is*
  the SL pool + collateral — so the "TP draws from the SL pool" behavior falls out for free. The order-field
  consume clamps to 0 (TP has none), so the **SL's `CurrentBuyReservation` field then lags** the true pool by
  `P·N`. The coordinator's inverted `OnChildFillAsync` **must shrink the SL field by the consumed buyback** to
  re-sync — this is the exact cash mirror of the long bracket's existing share-pool lag fix
  (`BracketCoordinator.cs:300–304`: "the TP fill already dropped Position.ReservedQuantity… only the SL's
  per-order field lags — bring it down"). Same transient-then-reconcile pattern; the reconciler tolerates the
  post-fill transient.
- **No-double-release, restated precisely:** both the buyback-consume and the collateral-release reduce the
  *same* `Fund.ReservedBalance` (not separate fields) — but they **partition** it: pool `S_worst·N` +
  collateral `entry·N`. Buyer-consume runs first (`:183`), drawing the pool portion; the collateral release
  (`:296–333`), clamped to `Min(ShortCollateral, ReservedBalance)`, takes the rest. They can't overlap. The
  "two buckets" are the *tracking fields* (`Σ CurrentBuyReservation` vs `Σ ShortCollateral`); the coordinator
  must keep the buy-reservation field in lock-step with the pool portion (above).

## Catch — uncapped market buy-stop SL is **unsizable** (fold into §3.2)
§3.2 sizes the SL pool `SL_worst = ` stop-limit price (bounded) **or** `trigger × (1 + slippageCap)`
(bounded). But `PlaceBracketAsync` currently lets an SL be an **uncapped stop-market** (no stop-limit, no
slippage cap ⇒ `Price 0`). For a *long* bracket that's fine — the SL reserves **shares** (qty-bounded, no
price risk). For a *short* bracket the SL is a **buy** whose cost has **no finite worst case** if it's an
uncapped market buy-stop (unbounded upward slippage) ⇒ **the cash pool can't be sized.** So short brackets
**must require the SL to be a stop-limit or a slippage-capped market buy-stop**; reject an uncapped market SL
at validation. (The TP-only fork has no SL, so it's unaffected — each TP reserves its own bounded buyback.)

## Needs a product decision (from the owner) — blocks the arm contract
- **Funding-failure at entry-fill (§3.1):** if available cash `< SL_worst·N` when the short opens, do we
  **(a) degrade to TP-only** (drop the SL, keep the profit-takers, alert "short now unprotected") — Ultraplan's
  recommended default — or **(b) reserve the SL pool at placement** (ties up `(entry+SL)·N` up front but
  guarantees the short is always protected), or **(c) cancel the whole bracket on open**? This changes the
  coordinator's arm contract, so it must be decided before the mechanical mirror.

## Net for the mechanical-mirror artifact
Proceed — the model is verified. Bake in: (1) the SL-field-lag re-sync in the inverted `OnChildFillAsync`
(mirror of `:300–304`); (2) reject uncapped market buy-stop SLs for short brackets; (3) the chosen
funding-failure policy. Everything else in §3 maps cleanly onto the long mirror.
