using KieshStockExchange.Helpers;
using KieshStockExchange.Models;
using KieshStockExchange.Services.BackgroundServices.Helpers;
using KieshStockExchange.Services.DataServices.Interfaces;
using KieshStockExchange.Services.MarketDataServices.Interfaces;
using KieshStockExchange.Services.MarketEngineServices;
using KieshStockExchange.Services.MarketEngineServices.Interfaces;
using KieshStockExchange.Services.PortfolioServices.Interfaces;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace KieshStockExchange.Tests;

/// <summary>
/// Byte-identity gate for bot-parallelism Phase 0 (<c>AiBotDecisionService.PrecomputeSharedTickCaches</c>).
/// The prepass warms the genuinely-shared per-stock tick caches (Fundamental/SeedPrice/OverBand{Buy,Sell})
/// in one deterministic pass BEFORE the bot sweep, replacing the prior lazy populate-on-first-read. Because
/// each cached value is a pure function of state frozen during the collect phase, eager warming must be
/// VALUE-IDENTICAL to lazy warming — otherwise a bot's OverBand veto / anchor read changes and the sim
/// diverges. The soak can't prove bit-identity, so this unit gate owns it.
///
/// Two arms:
///   • <see cref="Precompute_populates_shared_caches_for_every_listing"/> — the prepass fills every
///     (stock,ccy) with the correct value (SeedPrice == listing seed; OverBand verdict == the hand-computed
///     band check). Guards the direct regression risk (wrong currency, missed listing, wrong anchor source).
///   • <see cref="Precompute_matches_lazy_order_stream_and_caches"/> — driving <c>ComputeOrderAsync</c> over
///     a bot set with vs without the prepass yields the SAME ordered order stream, and every cache entry the
///     lazy path produced matches the eager path (the eager path is a value-identical superset).
///
/// Setup keeps the OverBand anchor fully controllable: <c>capFromSeed:true</c> (anchor = listing seed, which
/// the test sets), <c>StockProfileService(enabled:false)</c> (neutral mult = 1 ⇒ cap == overheatCap), and
/// RefillThrottle left off (ctx.RefillGate null ⇒ MoverGate is a no-op, matching the shipping config).
/// </summary>
public class BotPrecomputeEquivalenceTests
{
    private const CurrencyType USD = CurrencyType.USD;
    private const decimal OverheatCap = 0.10m;   // ±10% band around the seed anchor

    private sealed record Listing(int StockId, decimal Seed, decimal Price);

    // A calm in-band stock, a stock 30% above seed (over-band on the BUY side), and one 30% below
    // (over-band on the SELL side). Two currencies would also be exercised, but one keeps the math clear.
    private static readonly Listing[] Book =
    {
        new(10, Seed: 100m, Price: 100m),   // dev 0     ⇒ buy false, sell false
        new(11, Seed: 100m, Price: 130m),   // dev +0.30 ⇒ buy TRUE,  sell false
        new(12, Seed: 100m, Price: 70m),    // dev -0.30 ⇒ buy false, sell TRUE
    };

    // Builds the decision service + a fresh context with prices primed. One service instance is reused
    // across both arms of the equivalence test: it is stateless w.r.t. the per-tick caches (those live on
    // AiBotContext), and its sub-services are never Tick()'d here, so every read is deterministic.
    private static (AiBotDecisionService Svc, Func<AiBotContext> NewCtx) Build()
    {
        var profiles = new StockProfileService(enabled: false);

        var byId = new Dictionary<int, Stock>();
        var listingsByStock = new Dictionary<int, IReadOnlyList<StockListing>>();
        foreach (var b in Book)
        {
            byId[b.StockId] = new Stock { StockId = b.StockId };
            listingsByStock[b.StockId] = new List<StockListing>
            {
                new() { StockId = b.StockId, CurrencyType = USD, SeedPrice = b.Seed, IsPrimary = true },
            };
        }

        var stocks = new Mock<IStockService>();
        stocks.Setup(s => s.ById).Returns(byId);
        stocks.Setup(s => s.GetListings(It.IsAny<int>()))
              .Returns((int sid) => listingsByStock.TryGetValue(sid, out var ls)
                  ? ls : (IReadOnlyList<StockListing>)Array.Empty<StockListing>());
        stocks.Setup(s => s.IsListedIn(It.IsAny<int>(), It.IsAny<CurrencyType>()))
              .Returns((int sid, CurrencyType c) =>
                  listingsByStock.TryGetValue(sid, out var ls) && ls.Any(l => l.CurrencyType == c));

        var market   = new Mock<IMarketDataService>();
        var books    = new Mock<IOrderBookEngine>();
        var accounts = new Mock<IAccountsCache>();
        accounts.Setup(a => a.GetFund(It.IsAny<int>(), It.IsAny<CurrencyType>()))
                .Returns(() => new Fund { TotalBalance = 1_000_000m });
        accounts.Setup(a => a.GetPosition(It.IsAny<int>(), It.IsAny<int>())).Returns((Position?)null);

        var sentiment   = new BotSentimentService(stocks.Object, profiles, NullLogger<BotSentimentService>.Instance);
        var funds       = new FundamentalService(stocks.Object, profiles, NullLogger<FundamentalService>.Instance, enabled: false);
        var regime      = new BotRegimeService(NullLogger<BotRegimeService>.Instance);
        var activity    = new BotActivityService(stocks.Object, sentiment, NullLogger<BotActivityService>.Instance, recentReturn: _ => 0.0);
        var priceMemory = new BotPriceMemoryService(stocks.Object, NullLogger<BotPriceMemoryService>.Instance, priceLookup: _ => 0m);

        var svc = new AiBotDecisionService(
            market.Object, accounts.Object, books.Object, stocks.Object,
            sentiment, funds, profiles, regime, activity, priceMemory,
            NullLogger<AiBotDecisionService>.Instance,
            // Only the veto knobs matter here; everything else stays at its byte-identical default.
            overheatCap: OverheatCap, capFromSeed: true);
            // memoizeTickValues defaults true ⇒ the shared caches are actually written.

        AiBotContext NewCtx()
        {
            var ctx = new AiBotContext(accounts.Object) { TickId = 1, TickNowTicks = 0 };
            foreach (var b in Book) ctx.StockPrices[(b.StockId, USD)] = b.Price;
            return ctx;
        }

        return (svc, NewCtx);
    }

    [Fact]
    public void Precompute_populates_shared_caches_for_every_listing()
    {
        var (svc, newCtx) = Build();
        var ctx = newCtx();

        svc.PrecomputeSharedTickCaches(ctx);

        foreach (var b in Book)
        {
            var key = (b.StockId, USD);

            Assert.True(ctx.SeedPriceCache.TryGetValue(key, out var seed), $"SeedPriceCache missing {key}");
            Assert.Equal(b.Seed, seed);

            Assert.True(ctx.FundamentalCache.ContainsKey(key), $"FundamentalCache missing {key}");

            var dev = (b.Price - b.Seed) / b.Seed;
            Assert.True(ctx.OverBandBuyCache.TryGetValue(key, out var overBuy), $"OverBandBuyCache missing {key}");
            Assert.True(ctx.OverBandSellCache.TryGetValue(key, out var overSell), $"OverBandSellCache missing {key}");
            Assert.Equal(dev > OverheatCap,  overBuy);
            Assert.Equal(dev < -OverheatCap, overSell);
        }
    }

    [Fact]
    public async Task Precompute_matches_lazy_order_stream_and_caches()
    {
        var (svc, newCtx) = Build();
        var bots = MakeBots();

        var (lazyStream, lazyCtx)   = await SweepAsync(svc, newCtx(), bots, prepass: false);
        var (eagerStream, eagerCtx) = await SweepAsync(svc, newCtx(), bots, prepass: true);

        // 1) The order stream is byte-identical whether the shared caches were warmed eagerly or lazily.
        Assert.Equal(lazyStream, eagerStream);

        // 2) Every cache entry the lazy sweep produced is present with the SAME value under the prepass.
        //    (The prepass is a value-identical superset — it also warms listings no bot happened to touch.)
        AssertSubsetEqual(lazyCtx.SeedPriceCache,    eagerCtx.SeedPriceCache,    "SeedPriceCache");
        AssertSubsetEqual(lazyCtx.FundamentalCache,  eagerCtx.FundamentalCache,  "FundamentalCache");
        AssertSubsetEqual(lazyCtx.OverBandBuyCache,  eagerCtx.OverBandBuyCache,  "OverBandBuyCache");
        AssertSubsetEqual(lazyCtx.OverBandSellCache, eagerCtx.OverBandSellCache, "OverBandSellCache");
    }

    private static AIUser[] MakeBots() => new[]
    {
        MakeBot(1, 1001, new[] { 10, 11, 12 }),
        MakeBot(2, 1002, new[] { 11, 12 }),
        MakeBot(3, 1003, new[] { 10, 12 }),
    };

    private static AIUser MakeBot(int id, int seed, int[] watchlist)
    {
        var u = new AIUser
        {
            AiUserId = id, UserId = id, Seed = seed,
            StrategyCode = (int)AiStrategy.Random,
            IsEnabled = true,
            DecisionIntervalSeconds = 1,
            HomeCurrencyType = USD,
        };
        foreach (var s in watchlist) u.AddToWatchlist(s);
        return u;
    }

    // Mirrors the per-bot inner body of AiTradeService.CollectPendingOrdersAsync: (optionally) warm the
    // shared caches, then call ComputeOrderAsync for each bot in enumeration order, collecting the ordered
    // stream of produced orders. Registers the bots so ctx.GetRandom can seed each RNG deterministically.
    private static async Task<(List<string> Stream, AiBotContext Ctx)> SweepAsync(
        AiBotDecisionService svc, AiBotContext ctx, AIUser[] bots, bool prepass)
    {
        foreach (var b in bots)
        {
            ctx.AiUsersByAiUserId[b.AiUserId] = b;
            ctx.AiUsersByUserId[b.UserId] = b;
        }

        if (prepass) svc.PrecomputeSharedTickCaches(ctx);

        var stream = new List<string>();
        foreach (var b in bots)
        {
            var order = await svc.ComputeOrderAsync(ctx, b, b.HomeCurrencyType, CancellationToken.None);
            if (order is not null)
                stream.Add($"{order.Side}:{order.Entry}:{order.StockId}:{order.CurrencyType}:{order.Price}:{order.Quantity}");
        }
        return (stream, ctx);
    }

    private static void AssertSubsetEqual<TKey, TVal>(
        System.Collections.Concurrent.ConcurrentDictionary<TKey, TVal> lazy,
        System.Collections.Concurrent.ConcurrentDictionary<TKey, TVal> eager,
        string name) where TKey : notnull
    {
        foreach (var kv in lazy)
        {
            Assert.True(eager.TryGetValue(kv.Key, out var e), $"{name}: eager missing key {kv.Key} present in lazy");
            Assert.Equal(kv.Value, e);
        }
    }
}
