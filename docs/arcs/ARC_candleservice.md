# Arc: CandleService split (server)

**Target:** `KieshStockExchange.Server/Services/MarketDataServices/CandleService.cs` (1051 LOC,
`public sealed class CandleService : ICandleService, IDisposable`, namespace
`KieshStockExchange.Services.MarketDataServices`). **Lane:** Auto. **Branch:** `feature/bot-market-realism-v2`.
Auto-includes new .cs (no csproj edit). `InternalsVisibleTo KieshStockExchange.Tests` present.

## Split into TWO sub-arcs (per overview §4-B: byte-identical partial split FIRST, real-extract SECOND)

### Arc 2a — PURE PARTIAL SPLIT (byte-identical, this commit)
Add `partial` to the class; distribute members across 4 partial files, SAME namespace, interfaces
(`: ICandleService, IDisposable`) on the spine only. No method-body changes, no call-site changes.

- **`CandleService.cs`** (spine): all fields+ctor (EXCEPT GapLadder), Subscribe, UnsubscribeAsync,
  SubscribeAllAsync, SubscribeAllDefaultAsync, Dispose, OnTransactionTickAsync, OnTransactionTick,
  StreamClosedCandles, TryGetLiveSnapshot, GetOrAddAggregator/Ring/LiveStream, PersistAndPublishAsync,
  FlushLoopAsync, FlushClosedCandlesAsync, RebuildAggsByBook, RebuildSubscribedSnapshot. Keeps interfaces.
- **`CandleService.Read.cs`**: GetHistoricalCandlesAsync, FillGaps.
- **`CandleService.Maintenance.cs`**: GapLadder (static readonly), BackfillUpwardAsync,
  FillCandleGapsAsync, FillOneResolutionGapAsync, PrimeRingsAsync, FixCandlesAsync,
  ReplayTicksBuildClosed, FindMissingAndPersist, FindWrongAndPersist, EmptyCandlesReport,
  AggregateAndPersistRangeAsync.
- **`CandleService.Aggregation.cs`**: NewCandle, AggregateCandles, AggregateMultipleCandles,
  WeightedClose, AlignRange, CheckKey(x2), CheckOrdered, CheckContinuous.

Est: spine ~451 · Read ~76 · Maintenance ~332 · Aggregation ~192 — all under 500.

**Init-order safety (council check):** VERIFIED SAFE by Phase-0 — no field initializer references
another field/`this`/instance method; no static ctor; only GapLadder (static readonly, order-independent)
moves out of the spine. All instance fields stay in the spine.

### Arc 2b — REAL-EXTRACT CandleAggregationMath (separate commit, next)
Move the genuinely-pure static helpers (WeightedClose, CheckOrdered, CheckContinuous, AlignRange) into a
new `internal static class CandleAggregationMath` in `...MarketDataServices/Helpers/`. Update in-class call
sites to the qualified name. **Keep a `internal static ... WeightedClose(...) => CandleAggregationMath
.WeightedClose(...)` forwarder on CandleService** so `VwapCloseTests` stays byte-identical (the test hits
`CandleService.WeightedClose` via InternalsVisibleTo). Optionally reify AggregateCandles/AggregateMultiple
bodies as delegating wrappers — ONLY if test coverage confirms, else leave intact. Gate = build + FULL
suite (server oracle). NOT sorted-line-diff (bodies/call-sites change) → rely on the test suite.

## Gate (both sub-arcs, orchestrator re-runs independently)
1. Build server: `dotnet build KieshStockExchange.Server/KieshStockExchange.Server.csproj` green.
2. FULL `dotnet test` → 661/661 (VwapCloseTests = WeightedClose oracle).
3. 2a: moves-only sorted-line diff (each partial's members == lines removed from the original;
   original loses only moved members + gains `partial`). 2b: build+suite only.
4. 15m CK=0 smoke after the split lands (candles are read-only derived data, no Fund/Position touch →
   low blast radius, but run it per §6). CK/smoke checkpoint BEFORE starting arc 3 (AccountsCache).
