# Implementation brief — Arb scan optimization, Phase 2a + Phase 3

Engineer-ready spec for the approved plan (`docs/ultraplan-prompt-arb-optimization.md` lineage).
**Build + run the unit equivalence tests to green before any soak** — this is a determinism-critical
refactor and the unit tests are the byte-identity gate (the wall-clock soak cannot prove bit-identity).
All work is flag-gated, default-off, byte-identical-off. Land Phase 2a + Phase 3 as **one commit**, then
re-measure (pinned cap) before deciding on the optional Phase 2b / Phase 1.

Target file (unless noted): `KieshStockExchange.Server/Services/BackgroundServices/Helpers/ArbitrageDecisionService.cs`.

---

## Why (one paragraph)
The arb tick phase is ~190 ms and **scan-dominated**. `CollectOppsAsync` runs **per bot**, re-reading the
**same** 20 cross-listed books (5 bots × 20 stocks × 2 currencies = **200 book reads/tick**). All 5 arb
bots have identical watchlists and the per-book top is bot-independent, so the scan is ~80% redundant.
Phase 2a computes each stock's gap **once per tick** and shares it; Phase 3 removes the remaining cheap
redundancies (watchlist recompute, all-candidates flatten loop, per-read FX lookups).

## The determinism contract you must not break
`PickWeighted` (`:369`) draws a bot's RNG **once, only when `opps.Count > 1`**. If sharing the scan changes
any bot's `opps` count/order, its `ctx.GetRandom(aiUserId)` stream desyncs and the sim diverges. The subtle
trap: **each bot's own legs mutate the shared books before the next bot scans** (bot *i*'s round-trip/flatten
consumes a touch; today bot *i+1* re-reads and sees it). So a static once-per-tick map is wrong. The map
must be **incrementally self-invalidated**: after each bot acts, drop exactly the stocks it touched so the
next bot recomputes them fresh. This reproduces per-bot-fresh reads exactly.

---

## Phase 2a — incremental shared gap map (flag `Bots:Arbitrage:SharedScan`)

### Step 1 — extract `ComputeGap` (pure refactor, no behavior change)
Pull the body of `CollectOppsAsync`'s per-candidate loop into a helper that returns the raw, **pre-threshold**
best opp for one stock:

```csharp
// Bot-INDEPENDENT: depends only on the two book tops + FX bid. Returns null when neither
// direction is profitable. Do NOT apply MinArbitrageRatePrc here — that is per-bot (Step 3).
private async Task<Opp?> ComputeGap(int stockId, CancellationToken ct)
{
    var usd = await ReadTopAsync(stockId, CurrencyType.USD, ct).ConfigureAwait(false);
    var eur = await ReadTopAsync(stockId, CurrencyType.EUR, ct).ConfigureAwait(false);
    if (usd is null || eur is null) return null;
    var a = EvaluateDirection(stockId, usd.Value, eur.Value, CurrencyType.USD, CurrencyType.EUR);
    var b = EvaluateDirection(stockId, eur.Value, usd.Value, CurrencyType.EUR, CurrencyType.USD);
    return (a, b) switch
    {
        ({ } x, { } y) => x.Rate >= y.Rate ? x : y,
        ({ } x, null)  => x,
        (null, { } y)  => y,
        _              => (Opp?)null,
    };
}
```
Rewrite the **OFF** path of `CollectOppsAsync` to call `ComputeGap` so OFF stays byte-identical:
```csharp
foreach (var sid in candidates)
{
    var g = await ComputeGap(sid, ct).ConfigureAwait(false);
    if (g is { } o && o.Rate >= user.MinArbitrageRatePrc) opps.Add(o);
}
```

### Step 2 — add the map + generation guard (per-instance state)
`ArbitrageDecisionService` is a persistent singleton (`new`-ed once in `AiTradeService.cs:514`) and already
holds cross-tick state (`_nextConvertAt` at `:40`). Add there — **keep `Opp` private, add nothing to
`AiBotContext`**:
```csharp
private readonly Dictionary<int, Opp?> _gapMap = new();  // stockId -> best opp (null = no profit)
private long _gapMapTick = -1;                            // generation guard vs ctx.TickId
private bool _sharedScan;                                 // set from ctor flag (Step 5)
```
Reset per tick using the `WatchlistByBot` generation-guard idiom (**not** `ClearTickCaches`). At the very
top of `RunAsync` (`:71`), before the bot loop:
```csharp
if (_sharedScan && ctx.TickId != _gapMapTick) { _gapMap.Clear(); _gapMapTick = ctx.TickId; }
```
> Confirm `AiBotContext` exposes a monotonic per-tick `TickId` (used by `WatchlistByBot`, `AiBotContext.cs:163-169`).
> If the property has a different name, use it.

### Step 3 — flag-branch `CollectOppsAsync` to read through the map
`CollectOppsAsync` gains `ctx` (its only caller, `PrepareRoundTripAsync:206`, already has it — thread it
through). ON path preserves the bot's candidate order and applies the bot's threshold:
```csharp
private async Task<List<Opp>> CollectOppsAsync(AiBotContext ctx, AIUser user, List<int> candidates, CancellationToken ct)
{
    var opps = new List<Opp>();
    foreach (var sid in candidates)
    {
        Opp? g;
        if (_sharedScan)
        {
            if (!_gapMap.TryGetValue(sid, out g)) { g = await ComputeGap(sid, ct).ConfigureAwait(false); _gapMap[sid] = g; }
        }
        else g = await ComputeGap(sid, ct).ConfigureAwait(false);
        if (g is { } o && o.Rate >= user.MinArbitrageRatePrc) opps.Add(o);
    }
    return opps;
}
```

### Step 4 — intra-tick self-invalidation (the correctness core)
After each bot acts in the `RunAsync` loop (`:90-116`), remove from `_gapMap` exactly the stocks that bot
touched, so the next bot recomputes them:
- **Flatten:** change `TryFlattenAsync` (`:296`) to **return the stockId it actually sold** (`int?`, null when
  it placed nothing). The flatten loop (`:96-97`) collects each returned id.
- **Entry (inline):** `TryRoundTripAsync` (`:226`) — surface the acted `opp.StockId` (return `int?`, null when
  no round-trip fired).
- **Entry (batched, `BatchLegs` on):** after `ExecuteBatchedEntriesAsync` (`:255`), invalidate every pending
  entry's `opp.StockId`.
- In the loop, guard all removals with `if (_sharedScan)`:
```csharp
foreach (var sid in candidates)
{
    var flat = await TryFlattenAsync(ctx, user, sid, ct).ConfigureAwait(false);
    if (_sharedScan && flat is { } fs) _gapMap.Remove(fs);
}
...
if (_sharedScan && actedStockId is { } es) _gapMap.Remove(es);   // inline entry
```
One arb leg mutates exactly one `(stock,ccy)` (`WithBookLockAsync`, `OrderExecutionService.cs:191`) and both
legs of a round-trip share `opp.StockId`, so **one `Remove(stockId)` folds both currencies** — correct.

### Step 5 — flag plumbing (mirror `BatchLegs` exactly)
- Ctor (`:52-67`): add trailing `bool sharedScan = false`; assign `_sharedScan = sharedScan;`.
- Construction (`AiTradeService.cs:518`): add `sharedScan: _configuration.GetValue("Bots:Arbitrage:SharedScan", false)`.
- Config key already scaffolded in `appsettings.json` (this patch adds `SharedScan: false` + comment).

---

## Phase 3 — caching (same commit; all pure reads, byte-identical)

1. **Memoize `CrossListedWatchlist`** (`:135`). It calls `_stocks.IsListedIn` ×2/candidate/bot/tick and the
   cross-listed set is stable per run (rebuilt only on `CatalogChanged`). Cache per-bot (mirror
   `AiBotContext.WatchlistByBot`) or, since all arb bots share the identical watchlist, cache one result;
   invalidate on `IStockService.CatalogChanged`. Store on the persistent service.
2. **Gate the flatten loop** (`:96-97`) on a cheap "cohort holds any inventory" check. Arb round-trips net
   flat, so inventory only exists after a partial fill — the loop is almost always a no-op that still does 200
   book reads. Derive the gate from `AccountsCache` (do **not** shadow-store balances): skip a bot's flatten
   loop when it holds no open position. *Confirm the `IAccountsCache` API for enumerating a user's positions
   cheaply; if only per-(user,stock) `GetPosition` exists, keep the existing early-return but at minimum skip
   the two `ReadTopAsync` calls when `GetPosition(...).AvailableQuantity <= 0` (it already early-returns at
   `:299-300` before the reads — verify no read happens on the flat path, and this item may already hold).*
3. **Hoist FX** — `_fxRates.GetBidAsk`/`GetMidRate` are pure in-memory reads on a single EUR/USD pair constant
   within a tick. Snapshot once per tick (on the service, guarded by `_gapMapTick` or a sibling generation
   stamp) and reuse in `EvaluateDirection`/`TryFlattenAsync`/`MaybeRebalanceAsync`. Byte-identical (value can't
   change mid-tick).

---

## Tests (the determinism gate — must be green before any soak)
New file `KieshStockExchange.Tests/SharedScanEquivalenceTests.cs`, mirroring `GroupCommitEquivalenceTests.cs`.
- **Two worlds**, `SharedScan` off vs on, same seeded `AiBotContext` (identical `AIUser.Seed`s → identical
  `AiUserRngs`) and same `now`.
- **Fakes:** an `IOrderBookEngine` returning **mutable** `OrderBook`s and an `IOrderEntryService` whose
  `PlaceTrueMarket*` **consumes the touch** (reduces/removes the best level) so tops genuinely move as legs
  land — this reproduces intra-tick liquidity consumption, the thing self-invalidation must handle.
- **Capture + assert byte-identical:** the ordered stream of placement calls `(userId, stockId, qty, ccy, side)`
  **and** a per-bot RNG-draw counter (seam on `ctx.GetRandom`/`NextDouble`).
- **Adversarial fixture (the proof):** ≥3 arb bots with overlapping cross-listed watchlists; size a leading
  bot's leg to drain a stock's touch so a trailing bot's opps drop 2→1. Assert the ON (incremental map) stream
  equals the OFF (per-bot re-read) stream — identical draw counts. A naive share-once map fails this; the
  self-invalidating map passes.
- **Win assertion:** a read-counter on the fake engine shows reads(ON) ≪ reads(OFF).

---

## Verification checklist for local claude
1. `dotnet build KieshStockExchange.Server` and the test project — resolve any signature drift (the exact
   `ctx` threading into `CollectOppsAsync`, `TickId` name, `TryFlattenAsync`/`TryRoundTripAsync` return types,
   the `IAccountsCache` positions API).
2. `dotnet test --filter SharedScanEquivalenceTests` → green (byte-identical off/on, incl. the adversarial fixture).
3. Full arb suite green (`ArbBatchLegsEquivalenceTests`, `ShareConservationTests`).
4. **Soak** (Windows fleet): `scripts/kse-balance-soak-p.ps1 -Db baseline` vs `-Db lever` with
   `Bots__Arbitrage__SharedScan=true`, ≥45 min, **pinned cap** (`BotScalerService.Enabled=false` + a fixed
   `ActiveBotCap`). Harvest `python3 scripts/phase_harvest.py`. **Gate: `signals: CLEAN` both arms (CK=0) +
   arb-ms/tick down + comparable arb trade volume/fill rate.** (Do not expect bit-identical terminal state —
   wall-clock + the off-thread stop-promotion writer preclude it; the unit tests own byte-identity.)
5. **Re-measure decision:** build Phase 2b only if `arb-ms/tick` is still the dominant phase component or
   > ~30 ms after 2a+3. Expect 2a+3 to suffice (residual ≈ 40 O(1) peeks/tick).
