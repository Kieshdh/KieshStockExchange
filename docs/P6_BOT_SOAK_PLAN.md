# P6 — Bot realism + advanced-order soak. Scoping plan for Ultraplan to refine.

**Status:** plan only. Goal: drive the shipped advanced orders (P1 shorts, P2/P3 stops, P4 brackets,
P5 trailing, P5b short brackets) under **live bot flow** so the `ConservationProbe`/reservation reconciler
is the acceptance gate at scale — and, specifically, to **live-exercise the short-bracket scale-out
cushion + SL/TP fires** that self-trade prevention blocked from a deterministic admin-driven test.

**Scope note:** P6 touches the runtime **bot decision service** (`Services/BackgroundServices/...`), which
is normal app code — NOT the `/Tools` Python generation scripts (those stay out of scope per CLAUDE.md).

## Current state (grounded in code)
- `AiBotDecisionService.ComputeOrderAsync` (`AiBotDecisionService.cs:82`) is **stateless**: one Order (or
  null) per bot per tick, dimensions set directly, **always `Stop = StopKind.None`** (`:116`). Bots place
  plain market/limit buys & sells only.
- **Sells require holdings:** `ChooseStockId` for a sell only picks watchlist stocks with
  `AvailableQuantity > 0` (`:225–239`) — so **bots never short today**.
- **Submission path = batch matcher:** `AiTradeService` collects the computed orders and submits via
  `_marketOrders.PlaceAndMatchBatchAsync(orderList, ct)` (`AiTradeService.cs:514`). This matches plain
  market/limit orders. **It does NOT arm stops or place brackets.**
- Config: a `Bots:*` block (`appsettings.json:56`) threaded via `_configuration.GetValue("Bots:…")`.
- Telemetry: `BotEconomyTelemetry` / `EconomySample` (`BotEconomyTelemetry.cs:231`).
- Determinism: the seeded path uses `ctx.Decimal01(user.AiUserId)` / `ctx.GetRandom(...)` for every draw —
  any new randomness MUST go through these so the seeded run stays reproducible.

## The key constraint (shapes the phasing)
A **pure short open** is just a market/limit **sell by a flat seller** — it flows through the existing
`PlaceAndMatchBatchAsync` path unchanged (the §3.6 P1/H engine opens a cash-collateralized short). But
**stops, trailing stops, and brackets do NOT go through the batch matcher** — they need
`OrderEntryService.PlaceStop…/PlaceTrailingStop…/PlaceBracketAsync` (arm, don't match). So bot-placed
protective stops/brackets need a **separate submission route** from the batch tick. This is the main
design question for Ultraplan (§ open questions).

## Phased scope (recommend in order; each independently shippable + soakable)

### P6a — Bot short opens (cheapest, foundational)
- `ChooseOrderType`/`ChooseStockId`: add a **sentiment-gated short branch** — with prob `Bots:ShortProb`,
  a bearish bot picks a watchlist stock it is **flat** on (avoid the long→short flip = risk #7; flat-only
  keeps it a "pure" short) and emits a market/limit **sell** that opens a short.
- `ComputeOrderQuantityAsync`: size the short by **postable collateral** — `_accounts.GetFund(...).
  AvailableBalance − buffer`, capped by a new `Bots:ShortMaxExposurePrc` of portfolio (mirror of the long
  `PerPositionMaxPrc` room check, on the short side). Reuse `ShortCollateralForFill`-equivalent sizing.
- Submission: **unchanged** — pure shorts ride `PlaceAndMatchBatchAsync`.
- Telemetry: add short exposure + Σ collateral to `EconomySample`.
- Soaks: P1 shorts (open/extend), the H resting-short path if limit shorts are emitted. Gate: reconcile
  clean + no negative funds over a multi-hour run.

### P6b — Bot protective stops + trailing
- When a bot holds a position **without** an existing protective stop (detect via `ctx.OpenOrders`), with
  prob `Bots:StopLossProb` / `Bots:TrailingStopProb` emit a protective **StopMarket** (sell-stop below a
  long / buy-stop above a short) or **TrailingStop** at `Bots:StopOffsetPrc` / `Bots:TrailOffsetPrc`.
- **Needs the arm submission route** (not the batch matcher) — see open questions.
- Soaks: P2/P3 stops, P5 trailing under load.

### P6c — Bot brackets (long + short) — closes the short-bracket caveat
- With prob `Bots:BracketProb`, a bot opens a position **as a bracket** (entry + SL + 1–3 TPs), long or
  short. Different bots' fills cross each other's TPs/SLs (no self-trade), so this is what finally
  **live-exercises the short-bracket scale-out `cushionFreed` + SL/TP fires** end-to-end.
- Needs the `PlaceBracketAsync` submission route; sizing must fund the SL cash pool (short) / share pool
  (long); keep it "pure" (no flip).
- Soaks: P4 + P5b brackets at scale.

## Open questions for Ultraplan
1. **Submission route for stops/brackets (the big one).** Extend the bot tick to make per-bot
   `PlaceStop…/PlaceTrailingStop…/PlaceBracketAsync` calls alongside the batch matcher, or add a second
   pass? Keep it off the hot path and within the bot loop's existing concurrency/gating.
2. **Stateful "protect an unprotected position" detection.** The decision service is stateless one-order;
   P6b/c need to see existing open stops/brackets (`ctx.OpenOrders`) to avoid double-protecting. Define the
   check.
3. **Determinism.** Every new probability/size draw must use `ctx.Decimal01`/`ctx.GetRandom` so the seeded
   soak stays reproducible — confirm the draw order doesn't perturb existing seeded behavior.
4. **Risk #7 avoidance.** Keep bot shorts flat-only (no long→short flip) and bot brackets pure, per the
   original Patch-6 note.
5. **Soak acceptance gate.** Define it: reconciler "no mismatches" every pass over an N-hour run with
   shorts+stops+brackets live, zero `fund.IsValid()` violations / negative Available, ConservationProbe
   clean, and bounded bot error rates. (Mirror the existing perf-gate harness.)
6. **Config defaults.** Conservative starting probabilities so the soak ramps (e.g. ShortProb small) and
   can be cranked to stress.

## Recommendation
Land **P6a first** (cheap, no new submission path, soaks the foundational shorts), then **P6b**, then
**P6c** (the one that closes the short-bracket scale-out/fire verification gap). Each phase is its own
PR + soak.
