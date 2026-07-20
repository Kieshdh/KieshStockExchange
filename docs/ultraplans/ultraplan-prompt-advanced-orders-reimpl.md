# Ultraplan A — advanced-order arming re-implementation (the ~200ms/tick hotspot)

**Goal:** eliminate the per-arm sequential Postgres commit that makes the `adv` tick-phase ~200 ms (≈30–45% of the bot cap — measured: adv-OFF lifts
cap ~2,300 → ~3,000–3,350). Conservation (CK=0) is sacrosanct; keep it flag-gated + byte-identical off; preserve the seed-determinism contract
(per-bot aiUserId ordering). Prod is user-facing. **This is the deep re-implementation of ONE subsystem; the broader engine perf work is Ultraplan B.**

## ★ Diagnosis (code-grounded, from a deep-dive — build on this, don't re-derive)
- **The scan is NOT the problem.** Live stops are watched by `StopTriggerWatcher` (a `HostedServices/StopTriggerWatcher.cs` BackgroundService) reacting to
  `IMarketDataService.QuoteUpdated` on a separate thread — a lock-free two-tier `ConcurrentDictionary<(StockId,Ccy),<orderId,WatchedStop>>` price-comparison,
  no DB. Cheap. (It DOES lack a sorted price-ladder — O(all armed stops for that stock) per quote — a secondary item, not the ~200 ms.)
- **The `_phAdvUs` "adv" phase is the ARMING/SUBMIT cost.** `SubmitAdvancedAsync` (`AiTradeService.cs:1133`) partitions the per-tick advanced decisions;
  `BatchArms=true` (ALREADY BAKED) routes StopMarketSell/TrailingStopSell through `OrderEntryService.ArmStopSellBatchAsync` → one share-reserve loop + ONE
  bulk INSERT + ONE position-UPDATE tx for the whole cohort (cheap). **The RESIDUAL cost = the still-per-order types in `SubmitAdvancedPerOrderAsync`
  (`AiTradeService.cs:1339`): short-opens (flat market shorts), buy-stops (StopMarketBuy), brackets (seeded-ZERO today, so ~0).**
- **Per-arm root cost:** each non-batched arm runs a full `OrderSettler.SettleAsync` (`Settlement/OrderSettler.cs:248`): `EnsureLoadedAsync` +
  `AcquireFund/PositionGateAsync` (a per-user SemaphoreSlim + ConcurrentDictionary.GetOrAdd — uncontended on the single loop but allocates) +
  `BeginTransactionAsync` (open conn + BEGIN) + `CreateOrder` (INSERT…RETURNING) + `UpdateAllAsync` (UPDATE) + `CommitAsync` (WAL flush) = **3–5 Postgres
  round-trips + a commit per arm**; ~7–11 ms × 18–28/tick = ~200 ms. WAL flush alone is ~3–8 ms per root tx.
- **Why `BracketBatch` (off) didn't help + isn't the fix:** short-opens are MARKET orders — their cost is the match+settle group-tx, not the entry-insert,
  so batching the entry is spent (prior finding). The residual short-open cost is genuinely in the match+settle path.

## ★★ FINAL-REVIEW HARDENING (2nd council, code-verified — READ BEFORE the section below; it CORRECTS it)
- **Buy-stops are NOT "mirror sell-stops" — this is the most likely stall + CK trap.** `StopMarketBuy` (in `SubmitAdvancedPerOrderAsync`, ~AiTradeService.cs:1355)
  actually places a stop-LIMIT buy (`PlaceStopLimitBuyOrderAsync` → `ArmStopOrderAsync` → `engine.ArmStopAsync`) that reserves **CASH, not shares**.
  `OrderExecutionService.ArmStopBatchAsync` (~:1716) explicitly REJECTS buy-side because the cash-reservation logic differs fundamentally from the sell-stop
  share-reservation path. ⇒ build a SEPARATE **`ArmStopBuyBatchAsync` with a FUND-reserve pre-flight** (+ the `limitPrice = stopPrice × 1.005` computation +
  a direction-sanity check). Pattern-matching `ArmStopSellBatchAsync` directly → a CONSERVATION VIOLATION.
- **Short-opens: the ENTRY-batch ALREADY EXISTS** — `OrderEntryService.PlaceMarketShortBatchAsync` (~:659), wired via `SubmitMarketShortBatchAsync` (~:1224) →
  `engine.PlaceMarketShortBatchAsync` (~:678), gated behind `BracketBatch` (the measured "no-win": it batches ENTRY-inserts, but the per-short cost is the
  MATCH+SETTLE). ⇒ **the real work is inside `OrderExecutionService.PlaceMarketShortBatchAsync`: amortize the MATCH+SETTLE into ONE group-tx with per-order
  savepoints** (do NOT go looking in OrderEntryService). ⚠️ COLLATERAL-ATOMICITY TRAP (Contrarian): the short-collateral reservation write MUST be visible to the
  sell's gate check WITHIN the same savepoint scope, or two short-arms in one batch each pass the gate on STALE reserved-quantity → phantom oversell → double
  collateral liability, invisible to CK. Restructure to check-then-reserve inside the savepoint; do NOT naively share a commit group with plain orders.
- **★ SEQUENCE + SCOPE (final-council reconciliation): do A's CHEAP, CK-safe half FIRST, DEFER the hard half.** Slice 1 = the buy-stop fund-reserve batch
  (+ optional sorted price-ladder for `StopTriggerWatcher`) — a standalone `BatchBuyStops`-flagged commit, CK-soak (15m, CK=0), then A/B the `adv` ms at a 5-10%
  buy-stop population. Slice 2 (SEPARATE commit) = the short-open match+settle group-tx, CK-soak on a short-heavy fleet. **Ship the two slices as SEPARATE commits**
  (co-mingling makes a CK failure un-attributable). **DEFER Slice 2 until Ultraplan B's SPARSE ACTIVATION is measured** — sparse cuts arm-volume 80-90%, which may
  make the CK-risky short-open settlement rewrite unnecessary. Slice 1 (cheap) also de-noises Ultraplan B's Phase-0 profiler baseline, so land + bake it first.

## What the ultraplan must design (the re-implementation)
1. **Buy-stops (StopMarketBuy) — extend the entry-batching.** These are armed triggers (entry-insert, like sell-stops) but are NOT covered by `BatchArms`.
   Route them through the same batched-arm engine path (`ArmStopBatchAsync` / a StopMarketBuy analogue): one share/fund pre-reserve loop + one bulk INSERT +
   one position/fund UPDATE tx for the cohort. Tractable, mirrors the proven sell-stop batch. CK-safe (same group-tx + savepoint pattern).
2. **Short-opens (flat market shorts) — batch the MATCH+SETTLE.** The hard part: these immediately match+settle per-order. Options for the ultraplan to
   weigh: (a) a batched market-short settlement path (amortise the N short-opens this tick into one root tx with per-order savepoints — the same shape
   `MatchAndSettleAsync`'s group-tx uses for the normal batch phase, which the engine comment says needs `SettleOrderAsync/MatchAndSettleAsync` refactored to
   accept a cohort); (b) FOLD short-opens into the NORMAL batch phase (they're just market sells-with-collateral — can the plain-order batch route absorb them
   so they ride the existing group-commit instead of a private per-order tx?). Option (b) may be the cleanest "new implementation" — unify short-opens with
   the plain market-order path rather than a bespoke advanced route.
3. **(Secondary) sorted price-ladder for `StopTriggerWatcher`** — replace the flat per-(stock,ccy) bucket with a sorted structure so `OnQuoteUpdated` pops only
   crossed head-nodes (O(triggered)), not O(all armed). Not the ~200 ms, but removes a latent scan cost that grows with the armed-stop population.

## Constraints + gates
- **CK=0 is the hard gate** — the batched settlement MUST be conservation-clean (the existing `BatchArms` proves the group-tx + per-order savepoint pattern
  works; reuse it). Validate with the conservation soak (ConservationProbe / ReservationAuditor / CK_ checks) + the money-probe.
- **Flag-gated + byte-identical off** (like `BatchArms`) — a config flip reverts instantly.
- **Seed-determinism** — preserve per-bot aiUserId ordering (the arm builders + settle order are part of the determinism contract).
- **Measure** — A/B the `adv` phase ms + the cap, at scale; target: adv-phase → near-zero, cap → the adv-off ceiling (~+30–45%).

## Key files (deep-dive map)
`AiTradeService.cs` (SubmitAdvancedAsync :1133, SubmitAdvancedPerOrderAsync :1339, the tick phases) · `AiBotDecisionService.cs` (ComputeAdvancedDecisionAsync
:800, BuildShortOpenAsync/BuildProtectiveStopAsync/BuildCappedTriggerAsync) · `MarketEngineServices/OrderEntryService.cs` (ArmStopOrderAsync :222,
ArmStopSellBatchAsync :355, the short-open/buy-stop entry) · `MarketEngineServices/OrderExecutionService.cs` (ArmStopAsync :362, ArmStopBatchAsync :1694,
PlaceMarketShortBatchAsync) · `MarketEngineServices/Settlement/OrderSettler.cs` (SettleAsync :248, MatchAndSettleAsync) · `PortfolioServices/AccountsCache.cs`
(AcquireFund/PositionGateAsync :928/936) · `DataServices/PgDBService.Orders.cs` (CreateOrder :207) · `HostedServices/StopTriggerWatcher.cs` (the scan).
