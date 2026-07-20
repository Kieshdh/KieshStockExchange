# P5b mechanical mirror — verification vs live code (before implementing)

Sanity-checked Ultraplan's mechanical-mirror design against HEAD (`6e0b6cb`, which Ultraplan couldn't read).
**Verdict: all load-bearing claims hold; design is implementation-ready.**

## Reuse win #1 — TradeSettler needs no change. ✓
A buy fill pays via `buyerFund.ConsumeReservedFunds(notional)` at the **fund-aggregate** level
(`TradeSettler.cs:183`; `Reserved −= n; Total −= n`), and the per-order consume is clamped
`Min(notional, CurrentBuyReservation)` (`:200`). So a 0-reservation bracket TP draws its buyback from the
shared `Fund.ReservedBalance` (which holds the SL pool) with no underflow and consumes 0 from its own field.
The collateral release stays clamped to `Min(ShortCollateral, ReservedBalance)` (`:308–309`). Confirmed.

## Reuse win #2 — reconciler already counts the SL pool. ✓
`OrderRegistry.GetArmedBuyStopsForUser` filters `IsBuyOrder && IsArmed && IsStopOrder` (`:121–123`) → the
short-bracket SL (an armed buy-stop) is returned. `ClampFundAsync` folds it (`AccountsCache.cs:802`), and the
reconcile pass buy branch counts `o.IsArmed && o.IsStopOrder`. So `Σ buy-reservation + Σ short-collateral` is
already the fund expectation. Reconciler work = confirm + add fixtures, not new counting. Confirmed.

## The cushionFreed precision (1b) — traced, correct. ✓
Short N @ entry, SL pool `S·N` on `SL.CurrentBuyReservation`, collateral `entry·N`, so
`Fund.ReservedBalance = (S+entry)·N`. A TP closes `c` @ `Pf`:
- TradeSettler: `ConsumeReservedFunds(Pf·c)` (fund −= Pf·c) + pro-rata `UnreserveFunds(entry·c)` collateral.
- Coordinator 1b: `poolDrop = S·c`, `buyback = Pf·c` (already spent), `cushionFreed = (S−Pf)·c`,
  `UnreserveFunds(cushionFreed)`, `sl.ConsumeBuyReservation(S·c)` → `SL.CurrentBuyReservation = S·(N−c)`.
- Result: `Fund.ReservedBalance = (S+entry)·N − Pf·c − entry·c − (S−Pf)·c = (S+entry)·(N−c)` ==
  `Σ buy-reservation (S·(N−c)) + Σ collateral (entry·(N−c))`. **Invariant restored.** Omitting `cushionFreed`
  would strand `(S−Pf)·c` reserved → confirmed necessary. (Between the settler and the coordinator there's an
  under-reserve transient of `Pf·c` — report-only, resolves when 1b runs; same pattern as the long
  `BracketCoordinator.cs:300–304`.)
- Guards hold: `ConsumeReservedFunds(Pf·c)` needs `Reserved ≥ Pf·c` (pool+collateral cover it);
  `UnreserveFunds(cushionFreed)` and `ConsumeBuyReservation(S·c)` both within bounds.

## Decisions already baked (from §3 verification)
Degrade-to-TP-only on insufficient cash; reject uncapped market buy-stop SLs (unsizable pool); book→fund→
position gate via `AcquireUserGatesAsync`. No schema change (pool rides `CurrentBuyReservation`, in-memory).

## Build order (Ultraplan's, unchanged): validator → DTO/PlaceBracketAsync → coordinator arm (1a) →
coordinator close (1b–1d incl. cushionFreed) → cold-load `ReseedBracketCashPools` (+ ClampBuys skip bracket
children, in the shipped Q1/Q2 order) → reconciler fixtures. Tests: cash-pool sizing/lag unit math (the 1b
table), short-bracket smoke (place→scale-out→SL-fire→cancel), cold-load mid-bracket, conservation +
`fund.IsValid()`/`Reserved ≤ Total` assertions.
