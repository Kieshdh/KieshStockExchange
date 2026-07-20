# Bot-loop performance — Option C+ investigation worksheet (target: 20k bots)

Status: FILLED BY LOCAL CLAUDE (step 2). Companion to `docs/BOT_LOOP_CHECK_OFFLOAD_PLAN.md`, run after the
Option B patch (check-offload) merged. All PROD numbers captured 2026-06-09 ~15:12–15:27 UTC on the Netcup
box (commit `6b70767`, 20,005 AI users loaded, scaler fully ramped to cap 13k–19k) with
`Bots:PhaseTimingSeconds=30` enabled via an env-only compose override (no rebuild — the profiling code is
already in the prod image). Investigate-only: NO optimization was implemented.

> **HEADLINE FINDING (changes the plan — read first).** Post-Option-B, the steady per-tick span the scaler
> sizes against is **dominated by ENGINE SUBMISSION, not the per-bot decision arithmetic**:
> ```
> steady tick (maint-light, cap ~15k):  check 0.03 + collect ~31 + batch ~290 + adv ~245 + arb ~28  ≈ 594 ms
> scaler target = 60% × 1000ms TradeInterval = 600 ms   →  the cap is pinned by batch+adv, exactly on target
> ```
> `collect` (the per-bot decision path where Tier-2 items C2–C5 live) is only **~5% of the span (~31 ms)**.
> `batch`+`adv` are **~90%**. So **C2–C5 cannot meaningfully raise the 20k ceiling** — they optimise a phase
> that is already cheap. The real ceiling levers are in the engine path:
> - `batch` ≈ 290 ms for ~480 orders/tick ≈ **0.6 ms/order**.
> - `adv` ≈ 245 ms for ~49 advanced orders/tick ≈ **5.0 ms/order** — ~8× costlier, because advanced orders go
>   through the un-batched entry/arm route (`SubmitAdvancedAsync`, sequential `PlaceBracketAsync` /
>   `PlaceStopMarketSellOrderAsync`) instead of the batched matcher. **Batching the advanced entry route (or
>   lowering `Bots:Advanced:MaxPerTick`) is the single highest-value ceiling lever and is NOT a worksheet
>   candidate — flagging it for remote Claude.**
>
> `maint` (excluded from the EWMA by Option B, but still blocks the single loop thread and shows up in tick
> wall-time) is **bimodal**: a ~40–56 ms floor every 30 s (= the prune pass) plus a ~230–1240 ms spike every
> 60 s (= the asset reload + economy snapshot). So the maint items (C1/C6/C7) cut wall-time / hiccups, not
> the steady ceiling. **C7 (asset reload) is the maint heavyweight; C1 trims the economy snapshot; C6/prune
> is only ~40–56 ms.**

## How this document is used (round-trip protocol)

```
   ┌────────────────────────────────────────────────────────────────────────┐
   │ 1. REMOTE Claude (web)  →  writes this worksheet (the investigation)     │  ✅ done
   │ 2. LOCAL Claude (you)   →  investigate + measure on PROD, fill the       │  ✅ THIS PASS
   │                            "▶ REPORT BACK" blocks, commit, return        │
   │ 3. REMOTE Claude        →  refines the plan from the findings            │  ← next
   │ 4. REMOTE Claude        →  emits the final implementation PATCH file     │
   │ 5. LOCAL Claude         →  applies the patch, runs tests, deploys        │
   └────────────────────────────────────────────────────────────────────────┘
```

## Measurement harness — captured

Per-phase profiling enabled on PROD (config `Bots:PhaseTimingSeconds=30`). The `BotPhase` line reads
`check + collect + batch + adv + arb + recon + maint (ms/tick)`. Read via
`docker logs ...-server-1 | grep -E "BotPhase|Scaler"`.

```
▶ BASELINE (local Claude):
  date / commit: 2026-06-09, commit 6b70767 (master), env-only PhaseTimingSeconds=30 override
  active-bot cap during capture: oscillating 13,443 – 19,331 (scaler at target; loaded = 20,005)
  TradeInterval ms: 1000  (scaler target span = 60% = 600 ms)
  BotPhase ms/tick (representative maint-LIGHT window, cap 17,600):
      check 0.03  collect 31.90  batch 270.38  adv 301.55  arb 24.02  recon 0.00  maint 48.07   total 675.9
  BotPhase ms/tick (representative maint-HEAVY window, cap 17,765):
      check 0.03  collect 29.21  batch 238.74  adv 156.48  arb 33.77  recon 0.00  maint 1167.47 total 1625.7
  Steady-phase ranges across ~15 min (cap 13–19k):
      check 0.03–0.19 | collect 21–42 (μ≈31) | batch 192–449 (μ≈290) | adv 156–331 (μ≈245)
      arb 23–36 (μ≈28) | recon 0, then 77–701 every 5 min | maint 40–56 (light) / 251–1282 (heavy)
      orders ≈ 480/tick | advanced ≈ 49/tick (at the Bots:Advanced:MaxPerTick=50 cap)
  Scaler LastLoadFraction: targets 0.60; observed EWMA 335–899 ms over 1000 ms → load 34–90%, cap chases 60%
  cap trajectory over window: sawtooth 13.5k↔19.3k (healthy — Option B removed the old 3810↔9982 crater)
  Invariants at baseline — ConservationProbe: no violation lines in 16 min  CK_Funds/CK_Positions: none (clean)
    tests: 63/63 passing (KieshStockExchange.Tests, net9.0, local; no code changed this session)
```

**Determinism smoke (needed for all Tier 2 items):**

```
▶ DETERMINISM SMOKE (local Claude):
  No existing fixed-seed FLEET order-stream replay harness exists. The per-bot RNG IS deterministic
  (AiBotContext.GetRandom → new Random(DailySeed(user.Seed, AiUserId, Today)); Decimal01 advances it), but
  the live loop's emitted stream is NOT byte-reproducible run-to-run because order prices feed back through
  the matcher (StockPrices/SmoothedPrices come from fills), so a whole-fleet replay can't be pinned cheaply.
  Smallest viable guard for the Tier-2 exactness contract = a focused GOLDEN UNIT TEST (new, under
  KieshStockExchange.Tests): construct a synthetic AiBotContext with pinned StockPrices/SmoothedPrices/
  PreviousPrices/OpenOrders + a fixed Fund/Position + a fixed-seed Random, call ComputeOrderAsync (and
  ChooseOrderType / ChooseStockId / PickStock) BEFORE and AFTER each Tier-2 edit, and assert (a) the produced
  Order is identical AND (b) the number of RNG draws consumed is identical (capture via a counting Random
  wrapper). Because every Tier-2 edit only touches pure-read arithmetic BETWEEN ctx.Decimal01/rng.Next*
  draws, an unchanged draw-count + unchanged output is sufficient proof the seeded stream is byte-identical.
  Recommend remote Claude bundle this test into the C2–C5 patch as the gate.
```

---

## TIER 1 — Biggest win, lowest risk

### C1. `LogSnapshot`: walk held positions, not the whole stock universe
**File:** `BotEconomyTelemetry.cs:103` (the `foreach (var sid in _stocks.ById.Keys)` walk inside `LogSnapshot`).

```
▶ REPORT BACK (C1):
  StocksByUser semantics confirmed? YES. Built in AiBotStateService.RefreshAssetsAsync:103-108 from
    _db.GetPositionsForUsersAsync grouped by UserId → HashSet<stockId>. AiBotContext.PortfolioValueByCurrency
    (AiBotContext.cs:167-179) ALREADY iterates exactly StocksByUser[userId] then GetPosition per stock — the
    established precedent for the held-set walk.
  Behavioral consumers of the totals (esp. ArbThrottleEngaged): the ONLY behavioral consumer is
    ArbThrottleEngaged (BotEconomyTelemetry.cs:157-165) — it gates ArbitrageDecisionService via
    arbHouseFractionPct = (arbCohortWealthUsd + houseWealthUsd) / (totalWealthUsd + houseWealthUsd), and
    totalWealthUsd includes totalSharesUsd which the share-walk produces. Everything else (_samples queue,
    _store ndjson, the INFO log/Metrics) is pure telemetry that tolerates 60 s lag. The arb-cohort sub-total
    is also fed by this walk, but arb bots' held stocks are in StocksByUser too, so the held-set walk is
    exact for them as well (modulo the mid-window-fill staleness below — which the throttle tolerates).
  Does any fill-time hook update StocksByUser today? NO. ApplyResultToCache (AiBotStateService.cs:192-209)
    updates ONLY ctx.OpenOrders (adds the newly-resting limit). RecordTx (160-179) only does RecordTrade +
    burst. So a stock first ACQUIRED mid-window is absent from StocksByUser until the next 60 s
    RefreshAssetsAsync. NOTE: PortfolioValueByCurrency already lives with this exact approximation today, so
    the held-set walk inherits an EXISTING accepted staleness, it does not introduce a new one.
  Measured: position-walk ms within maint: not directly timed (prod-source edit was out of scope). STRUCTURAL:
    today the walk is _stocks.ById.Keys = 50 stocks × 20,005 bots = ~1.0M GetPosition lookups per snapshot
    (every 60 s); ~36.5 of the 50 return Quantity<=0 and are skipped AFTER the lookup. Held set is avg 13.53
    positions/bot (p95 22, max 28) → held-set walk ≈ 271k lookups → ~3.7× fewer lookups on the share-walk.
    The economy snapshot is the SECOND-largest maint component (refresh is first); on the maint budget this
    trims the ~50–150 ms economy portion of the 60 s spike to ~15–40 ms.
  Estimated stocks-per-bot held (avg/p95) on PROD: avg 13.53 / p95 22 / max 28 (universe = 50 stocks).
    Note ai_position_rows = 1,000,250 = exactly 50 × 20,005 → every bot has a Position ROW for all 50 stocks
    (seeded), but only ~13.5 are non-zero — which is why GetPosition is non-null for all 50 today.
  Go/no-go + recommended exactness fix: GO (low risk, ~3.7× cut on the economy walk). Exactness fix
    (add stockId to StocksByUser on fill): RECOMMENDED but optional — adding the placed/filled stockId to
    StocksByUser in ApplyResultToCache makes the held-set exact mid-window AND tightens the SAME staleness
    already present in PortfolioValueByCurrency (a free correctness bonus). Single-threaded, no new races.
    Strictly, the throttle tolerates 60 s lag, so it's a nice-to-have, not a blocker.
```

---

## TIER 2 — Steady per-bot path  ⚠️ RE-SCOPED BY MEASUREMENT

> **All of Tier 2 optimises `collect`, measured at only ~21–42 ms/tick (~5% of the 594 ms steady span).**
> Even a perfect collect→0 would move the cap by ~5%. These are still cheap, safe, correct wins worth taking
> (and C4 has a large *relative* cut on the sell path), but **they will not get the loop to 20k** — the
> ceiling is batch+adv (see headline). Recommend remote Claude treat C2/C3/C5 as low-priority polish and
> redirect ceiling effort to batching the advanced entry route. C4 is the one Tier-2 item with a meaningful
> absolute footprint and is kept.

### C2. Fuse the three watchlist passes in `ChooseOrderType`
**Files:** `AiBotDecisionService.cs:430` (`ComputeWatchlistMomentum`), `:450` (`AverageWatchlistSentiment`),
`:457` (`AverageWatchlistValueGap`).

```
▶ REPORT BACK (C2):
  Each helper RNG-free & side-effect-free? YES, all three.
    - ComputeWatchlistMomentum (AiBotContext.cs:194-212): reads SmoothedPrices/PreviousPrices only.
    - AverageWatchlistSentiment (AiBotDecisionService.cs:858-869): _sentiment.GetSentiment (read) +
      ctx.PersonalSentiment (pure hash — AiBotContext.cs:131-132 explicitly "Does NOT advance any Random").
    - AverageWatchlistValueGap (AiBotDecisionService.cs:873-887): Fundamental (read) + SmoothedPrices (read).
    None consume ctx.Decimal01/rng.Next* and none mutate state → fusing them into one watchlist pass cannot
    change the RNG stream.
  Other callers of each helper: each is called EXACTLY ONCE, only from ChooseOrderType, all over the same
    user.Watchlist. momentum + sentiment fire every decision; valueGap fires only when _valueAnchorStrength>0
    (prod has the value anchor ON — Bots:ValueAnchor:Strength>0 — so all three run → 3 passes today).
  Avg watchlist size on PROD: 17.45 (p95 25, max 28). So today ≈ 3 × 17.45 ≈ 52 stock-visits/decision with
    duplicate SmoothedPrices lookups; fused = ~17.45 visits, one lookup each.
  Go/no-go: GO (pure, safe) but LOW priority — saves a fraction of the ~31 ms collect. Bundle with C3/C5 and
    the C2–C5 golden test (determinism smoke).
```

### C3. Precompute the `1/pow(stockId, alpha)` weight table in `PickStock`
**File:** `AiBotDecisionService.cs:541` (`1.0 / Math.Pow(stockIds[i], RuntimeWeightAlpha)` per candidate).

```
▶ REPORT BACK (C3):
  Is RuntimeWeightAlpha mutable at runtime? NO. It's `private const double RuntimeWeightAlpha = 0.7`
    (AiBotDecisionService.cs:562) — a compile-time constant, no config binding, no setter → NO invalidation
    needed. A static readonly Dictionary<int,double> (or array indexed by stockId) computed once at first use
    fully replaces the per-candidate Math.Pow.
  Distinct stockId count (table size): 50 (the whole listed universe; PickStock weights by stockId only, the
    base weight is currency-independent). Trivially small table.
  Go/no-go + invalidation needed?: GO, no invalidation. Removes one Math.Pow(double) per candidate per
    decision (candidates = watchlist ≈ 17 on a buy). Tiny but free and safe. Bundle with C2/C5.
```

### C4. Hoist the repeated `ComputeCommitted*` order-walks within one decision  ← the one Tier-2 item worth real effort
**File:** `AiBotDecisionService.cs:727-758`, called from `ChooseStockId:516` and `ComputeOrderQuantityAsync:679,701,712`.

```
▶ REPORT BACK (C4):
  Calls-per-decision count (buy path / sell path):
    BUY path: ComputeCommittedBuyFunds ×1 (:679) + ComputeCommittedCoverShares ×1 only if the bot is short
      that stock (:701). So 1–2 walks of OpenOrders[userId].Values.
    SELL path: ComputeCommittedSellShares is called ONCE PER SELL CANDIDATE inside the ChooseStockId loop
      (:513-523) PLUS once in the sell branch of ComputeOrderQuantityAsync (:712). Sell candidates ≤ held
      positions with available qty (~≤13.5). So a sell decision does up to ~(candidates + 1) ≈ 14 walks,
      each O(open orders for that user) ≈ 48 → ~14 × 48 ≈ ~670 order-visits PER SELL DECISION.
  OpenOrders immutable mid-decision? YES. Single-threaded loop; OpenOrders is mutated only by
    ApplyResultToCache (between batch submissions) and RefreshAssetsAsync (maint pass) — never during a
    ComputeOrderAsync call. So one pass over OpenOrders[userId] at the top of the decision can serve every
    ComputeCommitted* consumer.
  Avg open orders per active bot on PROD: 47.69 (p95 106, max 175). 872,896 open orders fleet-wide.
  Go/no-go: GO — the strongest Tier-2 item. Precompute per-decision, once, from a single walk of
    OpenOrders[userId]: committedBuyFundsByCurrency, committedSellSharesByStock, committedCoverSharesByStock;
    pass them down. Collapses the sell path from O(candidates × orders) ≈ 670 visits to O(orders) ≈ 48 — a
    ~14× cut on the sell-decision committed cost. Pure read, RNG untouched (covered by the C2–C5 golden test).
    Caveat: absolute saving is bounded by the ~31 ms collect budget, so this trims collect (helps a bit) but
    does not lift the batch+adv ceiling.
```

### C5. Remove per-decision LINQ allocations
**Files:** `AiBotDecisionService.cs:505` (`watch.Where(IsListedIn).ToList()` — also at :246/:372/:382);
`AiTradeService.cs:721` (`pending.Select(x => x.order).ToList()`).

```
▶ REPORT BACK (C5):
  IsListedIn stability / how often it changes: _stocks.IsListedIn(stockId, currency) reflects StockListings
    (70 rows for 50 stocks → ~20 cross-listed in 2 currencies). Listings change only at admin/seed time, so
    the filtered watchlist is effectively STATIC within a session. The Where(IsListedIn).ToList() is rebuilt
    per call at 4 sites (ChooseStockId:505, BuildProtectiveStopAsync:246, FirstFlatStock:372,
    FirstLongableStock:382) → one List alloc per decision (× active bots/tick). Cacheable per (bot, currency);
    since bots decide only in HomeCurrency, effectively one cached filtered list per bot, invalidated on the
    60 s asset reload.
  PlaceAndMatchBatchAsync signature: Task<IReadOnlyList<OrderResult>> PlaceAndMatchBatchAsync(
    IReadOnlyList<Order> orders, CancellationToken ct) — IOrderExecutionService.cs:39. Takes IReadOnlyList<Order>.
  Measured GC/alloc pressure at fleet load: not directly obtainable without an on-box profiler (out of scope
    this pass). STRUCTURAL: the per-decision Where().ToList() (one per decision × ~active bots deciding/tick)
    is the meaningful alloc; the tick-level pending.Select().ToList() (AiTradeService.cs:721) is ONE alloc
    per tick (not per bot) → negligible.
  Go/no-go: PARTIAL GO — cache the IsListedIn-filtered watchlist per bot (kills a real per-decision alloc);
    SKIP the pending.Select().ToList() change (negligible, and it would entangle with C8's working-set). Note
    C2 + C3 + C4 + C5 all touch the same per-decision hot path — design them as one patch + one golden test.
```

---

## TIER 3 — Cheapen remaining maintenance walks (measured)

### C6. De-LINQ and bound `PruneWorstOrdersAsync`
**File:** `AiBotStateService.cs:238,247,268,271`.

```
▶ REPORT BACK (C6):
  Unbounded today? YES (with a caveat). PruneWorstOrdersAsync (AiBotStateService.cs:227-335) iterates ALL
    LOADED bots (line 231: ctx.AiUsersByAiUserId.Values — NOT the active cap; same loaded≫active waste as C8),
    and for each bot with open orders does userOrders.Values.Where(IsOpenLimitOrder).ToList() (:238),
    GroupBy currency (:247), then per group a foreach + .Sum (:268) + .OrderByDescending (:271). No per-pass
    cap. Worst-case orders scanned/pass ≈ loaded-with-orders × avg-open ≈ up to ~20k × ~48 ≈ ~960k visits +
    LINQ allocs, every 30 s.
  prune ms within maint on a prune cycle: ~40–56 ms (MEASURED — this is the maint-LIGHT floor that appears in
    every 30 s window with no 60 s cluster: observed 39.85 / 41.64 / 44.82 / 48.07 / 51.99 / 55.81). So prune
    is modest in absolute terms despite the big scan — most bots have few/no Far orders past budget, and the
    visits are cheap; the LINQ allocs are the main avoidable cost.
  Suggested PruneMaxPerPass: the worksheet's "max victims per pass" addresses the WRONG cost — the expense is
    the full SCAN, not the cancel count (cancels are already one batched call). Reframe: (a) de-LINQ the inner
    loop (manual sum/insertion instead of Where/GroupBy/Sum/OrderByDescending allocs), and (b) iterate the
    ACTIVE working-set only (C8 synergy), and optionally (c) round-robin a SHARD of bots per 30 s pass
    (e.g. 1/Nth of bots each pass) so no single pass scans the whole fleet.
  Go/no-go: GO for de-LINQ + active-only (low risk); the round-robin shard is a nice extra. Low priority by
    absolute ms (~40–56 ms), but cheap and it pairs naturally with C8.
```

### C7. Reassess `RefreshAssetsAsync` cadence / incrementality  ← the maint heavyweight
**File:** `AiBotStateService.cs:76-112`.

```
▶ REPORT BACK (C7):
  Readers of each index:
    - OpenOrders: HEAVILY read & load-bearing — CanPlaceMoreOrder (DecisionService:161), ChooseMarketMakerQuote
      (:491), ComputeCommittedBuyFunds (:729), ComputeCommittedCoverShares (:742), ComputeCommittedSellShares
      (:752), PruneWorstOrdersAsync (StateService:233/286/316). Written by ApplyResultToCache (:205-207).
    - StocksByUser: read by PortfolioValueByCurrency (AiBotContext.cs:170) (and would be by C1's economy walk).
    - CurrenciesByUser: ★ NO READER anywhere in the codebase ★ (grep-confirmed repo-wide: only declared in
      AiBotContext.cs:36, cleared in ClearAll, and built in RefreshAssetsAsync:96-101). It is DEAD — built
      every 60 s from a full GetFundsForUsersAsync query and never consumed.
  Is the full reload load-bearing or a safety net? LOAD-BEARING for OpenOrders + StocksByUser, NOT a pure
    safety net. ApplyResultToCache keeps OpenOrders correct for THIS bot's newly-placed resting limits, but
    there is NO cross-bot fill hook — when a resting limit is FILLED by another bot's incoming order, the
    maker's OpenOrders entry is NOT removed until the next RefreshAssetsAsync. So between reloads a maker's
    OpenOrders accumulates filled-away orders → ComputeCommitted* over-counts → the bot under-trades / drops
    sell candidates. The 60 s reload is the only thing that purges those. ⇒ blindly WIDENING the interval is
    NOT safe (under-trading drift). CurrenciesByUser, by contrast, is dead and can be removed outright.
  Per-query PROD latency / rows (GetFunds / GetPositions / GetOpenOrders for AI users):
    GetFundsForUsersAsync   → ~22,177 rows  (→ builds the DEAD CurrenciesByUser)
    GetPositionsForUsersAsync → ~1,000,250 rows (50 × 20,005 → builds StocksByUser)
    GetOpenOrdersForUsersAsync → ~872,896 rows (→ builds OpenOrders)
    Direct per-query ms not isolated (prod-source timing was out of scope), but the reload is unambiguously
    the maint heavyweight: it's the ONLY 60 s component heavy enough to explain the maint-HEAVY spike of
    ~230–1240 ms (= heavy-window maint minus the ~40–56 ms prune floor minus the small economy/stats parts).
  refresh ms within maint on a reload cycle: ~230–1240 ms (MEASURED as heavy-window maint − prune floor;
    high variance tracks DB latency on the 1.9M-row positions+orders pull). This is THE biggest single hiccup
    in the loop after recon.
  Go/no-go + safe approach:
    1) SAFE NOW (zero behavioral risk): DELETE the CurrenciesByUser index + its build loop + the
       GetFundsForUsersAsync call that feeds only it. Removes one of the three full-table queries every 60 s.
    2) DO NOT just widen the interval (OpenOrders staleness → under-trading).
    3) Bigger win, higher risk (leave for a later patch with an assertion): maintain OpenOrders incrementally
       — add a cross-bot "resting order filled/closed" hook so makers' OpenOrders entries are removed on fill,
       then the 60 s full reload becomes a safety net that can be widened, guarded by a periodic
       reconcile-vs-DB assertion. StocksByUser can likewise be kept incrementally (C1's exactness fix already
       proposes adding held stockIds on fill; removal-on-flat-out completes it).
```

---

## TIER 4 — Structural lever toward 20k

### C8. Does `CollectPendingOrdersAsync` walk all *loaded* bots vs. the active cap?
**File:** `AiTradeService.cs:670` iterates `_ctx.AiUsersByAiUserId.Values`.

```
▶ REPORT BACK (C8):
  How the cap is enforced (loaded set vs in-loop gate): IN-LOOP GATE over the FULL loaded set. The cap is a
    per-bot bool: ApplyActiveBotCap (AiBotStateService.cs:131-156) walks ALL loaded bots and sets
    user.IsEnabled = (enabled < cap) (arb cohort forced on, excluded from the count). CollectPendingOrdersAsync
    (AiTradeService.cs:670) then iterates ALL of _ctx.AiUsersByAiUserId.Values and does
    `if (!user.IsEnabled || !CanPlaceMoreOrder(...)) continue;` (:672) plus `if (Strategy==Arbitrage) continue`
    (:675). Same full-loaded walk in PruneWorstOrdersAsync (:231) and BotEconomyTelemetry.LogSnapshot
    (which doesn't even check IsEnabled — always full-fleet).
  On PROD: loaded vs active cap: loaded = 20,005; active cap oscillates 13.4k–19.3k → ratio ≈ 1.04–1.5×
    (arb cohort = 5, negligible). So at the CURRENT scaler equilibrium loaded is only modestly above active —
    the disabled-skip waste is small TODAY. It balloons only if the cap drops far below 20k (e.g. a heavy
    maint regime), where the loop would still pay O(20k) iteration to skip ~half.
  If loaded ≫ active: estimated wasted collect ms/tick: small at the current 1.04–1.5× ratio. The skip itself
    is cheap (a bool test; disabled bots short-circuit BEFORE CanPlaceMoreOrder). The larger collect cost is
    the ENABLED bots that pass the gate, roll RNG, then bail on the cheap continues (quiet period / decision
    interval / trade prob) — a working-set doesn't remove those.
  Feasible to iterate an active working-set without changing which bots trade? YES — maintain a
    List<AIUser> _activeBots rebuilt inside the existing ApplyActiveBotCap walk (enabled, non-arb), and
    iterate it in CollectPendingOrders + Prune. RISK: iteration ORDER feeds the batch order → matcher
    determinism; build the list in the SAME order as today's Dictionary.Values enumeration to preserve it.
  Go/no-go: GO but MODEST/low-priority at the current ratio. Its real value is (a) de-risking C6 (prune
    active-only) and (b) protecting collect/prune if the cap ever sits far below loaded. Rank below C1/C4/C7.
```

---

## What to return to remote Claude

```
▶ PRIORITY RANKING (local Claude, after measuring):
  Re-scoped by the headline finding (collect is only ~5% of the steady span; the ceiling is batch+adv).

  CEILING (to get past ~15–20k) — NOT worksheet candidates, flagged for remote Claude:
    A. Batch the advanced-order entry route (adv ≈ 5.0 ms/order × ~49/tick ≈ 245 ms; batch is 0.6 ms/order).
       Either batch PlaceBracket/PlaceStopMarketSell submissions or lower Bots:Advanced:MaxPerTick. Biggest
       single ceiling lever.
    B. Reduce/þatch the matched order volume or per-order matching cost in PlaceAndMatchBatchAsync
       (batch ≈ 290 ms for ~480 orders/tick).

  WALL-TIME / HICCUPS (measured maint + recon) — worksheet candidates, do these:
    1. C7 (safe slice): delete the DEAD CurrenciesByUser index + its GetFundsForUsersAsync query. Zero risk,
       removes 1 of 3 full reload queries every 60 s. Then (later/riskier) incremental OpenOrders to shrink
       the ~230–1240 ms refresh spike — the loop's biggest hiccup after recon.
    2. C1: held-set economy walk (50→~13.5 lookups/bot, ~3.7×) — trims the 2nd-largest maint component; low
       risk; pairs with C7's StocksByUser-on-fill exactness fix.
    3. C6: de-LINQ + active-only prune (~40–56 ms floor; cheap, pairs with C8).

  STEADY-PATH POLISH (collect ~31 ms; correctness/safety wins but won't lift the ceiling):
    4. C4: precompute committed maps once per decision (~14× cut on the SELL path). Best Tier-2 item.
    5. C2 + C3 + C5: fuse watchlist passes / weight table / cache filtered watchlist — bundle, one golden test.
    6. C8: active working-set — modest now (ratio 1.04–1.5×); mainly de-risks C6 and future low-cap regimes.

  Notes / surprises / anything that changes the approach:
    - Option B is fully validated on PROD: check fell to ~0.03 ms and the cap holds 13–19k (old crater gone).
    - THE BIG SURPRISE: the original Tier-2 premise (collect/decision cost is the ceiling) is wrong on prod —
      collect is ~31 ms (~5%). The ceiling is engine submission (batch+adv ≈ 90% of the 594 ms span). Remote
      Claude should redirect ceiling effort to candidate A (batch the advanced route) before C2–C5.
    - recon (ReservationAuditor, every 5 min) costs 77–701 ms when it fires — a periodic hiccup worth a glance
      (first post-ramp run was 701 ms, later ones 77–109 ms). Not in this worksheet's scope but visible.
    - PhaseTimingSeconds was left ENABLED on prod for capture; local Claude will turn it back off after this
      hand-off (env-only override removed; no rebuild).
```
