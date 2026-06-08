# P5b — Short brackets. Plan for Ultraplan to refine (reservation design FIRST).

**Status:** plan only — not implemented. Branch `feature/advanced-orders-p4-brackets`. This is the most
conservation-critical piece left in the advanced-orders arc: the **inverted, cash-side reservation model**.
Recommend Ultraplan produce the **reservation/collateral design (§3) first**, then the mechanical mirror.

## 1. Shape (web-confirmed; matches the long bracket, mirrored)
A short bracket opens a **cash-collateralized short** (the §3.6 P1/H model) and protects it with legs that
**buy to close**:
- **Parent:** a SELL (market or limit) — opens/extends the short; posts short collateral at fill (P1).
- **SL:** a **BUY-stop ABOVE entry** — price rises through it ⇒ buy back at a loss (caps the loss).
- **TPs:** **BUY-limits BELOW entry**, strictly **descending** — price falls ⇒ buy back at a profit.
- OCO-grouped, armed as the parent fills, exactly like the long bracket but with Side flipped.

## 2. What exists (long bracket, to mirror) — grounded in code
- `OrderEntryService.PlaceBracketAsync` (`OrderEntryService.cs:317-411`): builds `Side=Buy` parent +
  `Side=Sell` SL/TPs; long geometry (SL below entry, TPs above). Calls `_engine.PlaceBracketAsync`.
- `BracketGeometryValidator.Validate` (`Helpers/BracketGeometryValidator.cs`): SL below entry; TPs above,
  strictly ascending; qty>0; Σ TP qty ≤ parent qty.
- `BracketCoordinator` — **long-only** (`BracketCoordinator.cs:27`, and `:141` `if (!parent.IsBuyOrder) return;`).
  Arms via **`pos.ReserveStock`** (`:176`, `:250`) — Model B: SL owns the whole **share** pool
  (`sl.Quantity = CSR + AmountFilled`, TPs reserve 0); TP-only: each TP reserves its own shares. Resizes on
  TP fills (`OnChildFillAsync:298-320`) and SL fire (`OnStopFiringAsync`). Invariant: `Σ CSR ==
  Position.ReservedQuantity`.
- **Buy-to-close settlement already exists** (`TradeSettler.cs:291-333`): when a buyer was short, the fill
  releases short collateral from `Fund.ReservedBalance` pro-rata and `Position.ReleaseShortCollateral`. This
  is the P5b legs' fill path — it already releases collateral; the open question is how it composes with the
  **leg's own buy reservation** (§3).
- Cold-load reseed for brackets now exists (`AccountsCache.ReseedBracketReservations`, just shipped for Q1) —
  but it reseeds the **share** pool; P5b needs a **cash**-pool mirror, composed with `ClampBuys` +
  `BackfillShortCollateral` ordering.

## 3. THE CORE DESIGN QUESTION — inverted (cash) reservation model
A long bracket's legs reserve **shares** (`Position.ReservedQuantity`, qty-only). A short bracket's legs
are **buys** that reserve **cash** (`Fund.ReservedBalance`). The hard part: that same `Fund.ReservedBalance`
**already holds the short's P1/H collateral** `C` (posted when the parent SELL fills) **and** ordinary buy
reservations. So the leg reservations are entangled with the collateral on one fund. Sizing them naively
(each buy leg reserves its full notional) would **double-count** against the collateral and break
`Σ holds == Fund.ReservedBalance`.

Economic frame (per share, short opened at `entry`):
- Short open credits ≈ `entry` to TotalBalance and reserves collateral ≈ `entry` (buying power neutral).
- Buy back at SL price `S > entry` costs `S` ⇒ **needs `S − entry` MORE** than the collateral (the loss).
- Buy back at TP price `T < entry` costs `T` ⇒ **needs `entry − T` LESS** than the collateral (the profit).

So the questions for Ultraplan to resolve precisely (with the exact `Σ == Fund.ReservedBalance` invariant):
1. **Pool sizing & ownership.** Is there a Model-B mirror where the **SL owns the cash pool**? The pool is
   price-dependent (buy-back cost varies by leg price), unlike the qty-only share pool — so "SL owns the
   pool, TPs reserve 0" may not transfer cleanly. Define what each leg reserves.
2. **Incremental-over-collateral.** Does the SL reserve only the **incremental** cash beyond the held
   collateral — i.e. the potential loss `(S − entry) · qty` — drawn from `AvailableBalance` at arm, since the
   collateral already covers the `entry`-priced buy-back? Spell out the arithmetic and the invariant.
3. **TP-only short bracket.** TPs buy back BELOW entry (cost < collateral) ⇒ do they reserve **zero** extra
   cash (the collateral covers them)? Confirm, mirroring the long TP-only fork.
4. **Fill/cover composition.** When a buy leg fills it must: (a) consume its own buy reservation, (b) release
   short collateral pro-rata (existing `TradeSettler:291-333`), (c) shrink the short. Ensure these three
   compose with **no double-release** and `Σ == Fund.ReservedBalance` preserved — and that the coordinator's
   OnChildFill mirror (shrink the SL cash pool) doesn't fight the P1 collateral release.
5. **Over-cover / flip guard (risk #7 mirror).** Bound the buy legs to the short size so a leg can't buy past
   flat into a long (mirror of the long bracket bounding to `held`).

## 4. Mechanical touchpoints (after §3 is settled)
- `PlaceBracketAsync` (entry + `OrderExecutionService`): add the short mirror — `Side=Sell` parent,
  `Side=Buy` SL/TPs, a `side`/short flag on the path + `PlaceBracketRequest`.
- `BracketGeometryValidator`: side-aware — SL **above** entry, TPs **below** strictly **descending**.
- `BracketCoordinator`: drop the `IsBuyOrder`-only guard; add the cash-reserving mirror of
  `OnParentFillAsync` / `OnChildFillAsync` / `OnStopFiringAsync` / `OnMemberCancelledAsync` using
  `ReserveFunds`/`UnreserveFunds` + the §3 pool model, composed with the P1 collateral release.
- `AccountsCache` cold-load: a cash-pool `ReseedBracketReservations` mirror, ordered with the existing
  `ClampBuys` (collateral-aware, runs last) + `BackfillShortCollateral` passes (the P5/Q2 ordering).
- Reconciler/clamp: count the short bracket's fund-side leg reservations (mirror of how armed sell-stops +
  long-bracket pools are counted on the position side) so they aren't read as phantom and clamped away.
- UI (owner's): flip `ShowBracket` to allow Sell; side-aware captions + price-side validation.

## 5. Verification
- Unit: cash-pool sizing math (SL above entry incremental, TP-only zero-extra) — pure, like `TrailMath`/
  `ColdLoadReseedTests`. Coordinator arm/fill/fire resize keeping `Σ buy-reservation + collateral ==
  Fund.ReservedBalance`.
- Harness (`scripts/kse-order-smoke.ps1` style): place a short bracket (market + limit parent); parent
  fills → legs arm reserving the right cash; scale out via TP buys (collateral releases, fund reservation
  shrinks); fire the SL (buy-stop) → covers at a loss; cold-load mid-bracket → reseeds cash pool + collateral.
- **ConservationProbe is the gate** (cash side this time) — but note the long-bracket lesson: it's blind to
  conservation-clean structural defects, so also assert order-status + fund-validity (Available ≥ 0,
  `CK_Funds_Balance_Invariants`) like the Q1/Q2 cold-load tests.

## 6. Sequencing
Big, conservation-critical, and it interlocks with P1/H collateral + the cold-load passes I just shipped.
Recommend: **(a) Ultraplan delivers the §3 reservation/collateral design + invariant**, I sanity-check it
against the live collateral code, **then (b) the implementation plan**. Trailing-stop-limit is a smaller,
independent follow-up that can slot in before or after.
