# Overnight session notes (2026-06-06 → 07) — for review tomorrow

Autonomous run: implement + self-test Batch H Parts 3–7 (resting shorts), engine-only. UI deferred to
you. Anything needing your input or an Ultraplan look is logged below (not acted on).

## Goal
Wire Batch H Parts 3–7 from the blessed v2 design (gating fund→position; place-vs-fill in Part 4),
build, and verify with `kse-order-smoke.ps1` (extended with resting-short scenarios) + ConservationProbe.

## Progress log
- Read all of H's touch points (OrderSettler, SellerCapacityValidator, TradeSettler fill+scope+restore,
  OrderCanceller, AccountsCache reconcile/clamp/cold-load, OrderRegistry). Full edit plan mapped.
- Implementing Parts 3-7 (see commits). Key resolved details beyond the design:
  - Validator (Part 3): a PURE limit short (flat seller, no position) hits the no-position hard-fail
    BEFORE the flip branch — so extend BOTH `isShort` (startQty<=0, limit+collateral) and `isFlip`
    (startQty>0, limit+collateral). Detector = `IsLimitOrder && CurrentShortCollateral > 0`.
  - Reconcile/clamp (Part 7): resting-short collateral is on the ORDER (CurrentShortCollateral), not a
    position — fold into `expectedBalByFund` in the pass AND `ClampFundAsync` (new registry method
    GetOpenShortSellsForUser) or the clamp will erase the hold. Pure short has CSR=0, so the collateral
    fold is an INDEPENDENT check, not gated on CSR>0.
  - Cold-load re-seed runs single-threaded (no per-user gate), like BackfillShortCollateral — direct
    `fund.ReservedBalance += C`. Handle both the over-reserve split AND the no-position pure short.

## Needs your input (decide tomorrow)
- _(none)_

## For Ultraplan to review (do NOT act tonight)
- **Cold-load vs active bracket legs (defensive guard added, but please confirm).** My new
  `BackfillRestingShortCollateral` skips `o.IsBracketChild` so an active bracket TP (an Open limit sell
  whose shares are pooled on its sibling SL, CSR may be 0) is never mis-read as a resting short. I could
  NOT live-test a bracket whose entry filled and then survived a restart (BracketCoordinator rehydrated 0
  parents in all my runs). Worth an Ultraplan pass on: does an *active* (post-entry-fill) bracket leg flow
  through AccountsCache cold-load (ClampSells covered-seed) correctly, independent of the resting-short path?
- **ClampBuys TotalBalance cap ignores reserved short collateral.** `ClampBuysToFundBalance` caps buys at
  `reserved(buys) <= fund.TotalBalance`, not counting a resting short's (or filled short's) collateral
  already held. Pre-existing for filled-short collateral too (BackfillShortCollateral also runs after
  ClampBuys). Not a conservation bug (seeds match order fields), but on a tight account buys+collateral
  could exceed Total → negative Available after restart. Low priority; flagging for completeness.

## Results / verification
**Batch H Parts 3–7 (resting shorts) IMPLEMENTED + SELF-TESTED. All green.**
- Build: server + tests clean. Unit tests 29/29.
- Extended `kse-order-smoke.ps1` (added Step 7 resting-short): **38/38 pass**, run 3×.
  - Resting limit short straddle (sell 44 = 41 covered + 3 short): rests Open; fund.ReservedBalance rose
    by EXACTLY the short collateral (price×shortQty); covered shares reserved on the position; cancel
    released both back to baseline.
- Cold-load (restart) survival of a resting short: order stays Open, covered shares + collateral
  re-seeded; ConservationProbe (reconcile) **clean with the short open**.
- ConservationProbe "no mismatches" across every reconcile pass throughout.

**BUG FOUND + FIXED during testing (cold-load collateral clobber):**
- Symptom: place resting short → restart → cancel drained an UNRELATED open buy's fund reservation to 0.
- Root cause: cold-load reseeded resting-short collateral inside `ClampSellsToPositionQuantity`
  (`fund.ReservedBalance += C`), but `ClampBuysToFundBalance` runs AFTER and ASSIGNS
  `fund.ReservedBalance = <buy total>`, clobbering the collateral. The order kept CSR=collateral while
  the fund lost it, so the cancel's release drained the buy's reservation instead.
- Fix: split it like the existing P1 `BackfillShortCollateral` — ClampSells seeds only covered SHARES;
  a new `BackfillRestingShortCollateral` pass reseeds the collateral with `+=` AFTER ClampBuys. Verified:
  place→restart→cancel now leaves DB fund.reserved=119.44 (the buy) instead of 0.
- Note: the funds/positions/orders **API endpoints read the DB directly** (not AccountsCache), and
  cold-load reseeds the cache without persisting funds/positions — so post-restart API reads are stale
  until the next place/cancel/fill/clamp persists. The reconciler (cache-based) is the real conservation
  check. This is pre-existing P1 behavior (BackfillShortCollateral does the same), not introduced here.
