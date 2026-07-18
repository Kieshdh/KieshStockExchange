using KieshStockExchange.Helpers;
using KieshStockExchange.Models;
using KieshStockExchange.Services.BackgroundServices.Helpers;
using KieshStockExchange.Services.DataServices.Interfaces;
using KieshStockExchange.Services.MarketDataServices.Interfaces;
using KieshStockExchange.Services.MarketEngineServices;
using KieshStockExchange.Services.MarketEngineServices.Helpers;
using KieshStockExchange.Services.MarketEngineServices.Interfaces;
using KieshStockExchange.Services.PortfolioServices.Interfaces;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace KieshStockExchange.Tests;

/// <summary>
/// Byte-identity gate for Phase 2a (<c>Bots:Arbitrage:SharedScan</c>) in
/// <see cref="ArbitrageDecisionService"/>. The shared per-tick gap map must produce the SAME ordered
/// stream of engine placements as the per-bot fresh-scan path (OFF) — otherwise a bot's <c>opps</c>
/// count/order changes and its <c>ctx.GetRandom</c> draw desyncs the sim.
///
/// The soak cannot prove bit-identity (wall-clock + the off-thread stop-promotion writer perturb both
/// arms), so this unit gate owns it. The KEY case is the ADVERSARIAL drain: a leading bot's leg consumes
/// a stock's touch so a trailing bot's opps would drop — a naive share-once map keeps the stale gap and
/// diverges; the incrementally self-invalidating map recomputes and matches OFF.
///
/// Harness = a deterministic FakeMarket: each book read rebuilds a fresh OrderBook from mutable level
/// state (so no OrderBook internals are mutated), every place is recorded, and a place CONSUMES the
/// touched top so later scans in the tick see the moved book. Fills are reported as 0 (empty txs) — the
/// consumption alone drives the scan divergence, applied identically to both arms, isolating the map.
/// </summary>
[Collection("ClockSerial")]
public class SharedScanEquivalenceTests
{
    private const CurrencyType USD = CurrencyType.USD;
    private const CurrencyType EUR = CurrencyType.EUR;

    private sealed class Level { public decimal BidPx; public int BidQty; public decimal AskPx; public int AskQty; }

    private sealed class FakeMarket
    {
        public readonly Dictionary<(int, CurrencyType), Level> Books = new();
        public readonly List<string> Placements = new();
        public int Reads;
        private int _nextId = 1;

        public OrderBook Build(int sid, CurrencyType ccy)
        {
            Reads++;
            var book = new OrderBook(sid, ccy);
            if (Books.TryGetValue((sid, ccy), out var l))
            {
                if (l.BidQty > 0)
                    book.UpsertOrder(new Order { OrderId = _nextId++, UserId = 9001, StockId = sid, CurrencyType = ccy,
                        Side = OrderSide.Buy, Entry = EntryType.Limit, Stop = StopKind.None, Quantity = l.BidQty, Price = l.BidPx });
                if (l.AskQty > 0)
                    book.UpsertOrder(new Order { OrderId = _nextId++, UserId = 9002, StockId = sid, CurrencyType = ccy,
                        Side = OrderSide.Sell, Entry = EntryType.Limit, Stop = StopKind.None, Quantity = l.AskQty, Price = l.AskPx });
            }
            return book;
        }

        public OrderResult Buy(int userId, int sid, int qty, CurrencyType ccy)
        {
            Placements.Add($"B:{userId}:{sid}:{ccy}:{qty}");
            if (Books.TryGetValue((sid, ccy), out var l)) l.AskQty = Math.Max(0, l.AskQty - qty); // consume the touch
            return OrderResultFactory.Success(new Order { OrderId = _nextId++, UserId = userId, StockId = sid, CurrencyType = ccy,
                Side = OrderSide.Buy, Entry = EntryType.Market, Stop = StopKind.None, Quantity = qty }, new List<Transaction>());
        }

        public OrderResult Sell(int userId, int sid, int qty, CurrencyType ccy)
        {
            Placements.Add($"S:{userId}:{sid}:{ccy}:{qty}");
            if (Books.TryGetValue((sid, ccy), out var l)) l.BidQty = Math.Max(0, l.BidQty - qty);
            return OrderResultFactory.Success(new Order { OrderId = _nextId++, UserId = userId, StockId = sid, CurrencyType = ccy,
                Side = OrderSide.Sell, Entry = EntryType.Market, Stop = StopKind.None, Quantity = qty }, new List<Transaction>());
        }
    }

    private static AIUser MakeArbUser(int id, int seed, int[] watchlist)
    {
        var u = new AIUser
        {
            AiUserId = id, UserId = id, Seed = seed,
            StrategyCode = (int)AiStrategy.Arbitrage,
            IsEnabled = true,
            DecisionIntervalSeconds = 1,
            MinArbitrageRatePrc = 0.001m,
            MaxInventoryPerStock = 100,
            ConversionCadenceSeconds = 0,   // skip the FX rebalance path (no _portfolio calls)
        };
        foreach (var s in watchlist) u.AddToWatchlist(s);
        return u;
    }

    // Drives one arb tick and returns the ordered placement stream + the book-read count.
    private static async Task<(List<string> Placements, int Reads)> RunTick(
        bool sharedScan, Func<FakeMarket> marketFactory, Func<AIUser[]> usersFactory)
    {
        var market = marketFactory();

        var accounts = new Mock<IAccountsCache>();
        accounts.Setup(a => a.GetFund(It.IsAny<int>(), It.IsAny<CurrencyType>()))
                .Returns(() => new Fund { TotalBalance = 1_000_000m });
        accounts.Setup(a => a.GetPosition(It.IsAny<int>(), It.IsAny<int>())).Returns((Position?)null);

        var books = new Mock<IOrderBookEngine>();
        books.Setup(b => b.GetAsync(It.IsAny<int>(), It.IsAny<CurrencyType>(), It.IsAny<CancellationToken>()))
             .Returns((int sid, CurrencyType ccy, CancellationToken _) => Task.FromResult(market.Build(sid, ccy)));

        var entry = new Mock<IOrderEntryService>();
        entry.Setup(e => e.PlaceTrueMarketBuyOrderAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(),
                It.IsAny<decimal>(), It.IsAny<CurrencyType>(), It.IsAny<CancellationToken>()))
             .Returns((int u, int s, int q, decimal _, CurrencyType c, CancellationToken __) => Task.FromResult(market.Buy(u, s, q, c)));
        entry.Setup(e => e.PlaceTrueMarketSellOrderAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(),
                It.IsAny<CurrencyType>(), It.IsAny<CancellationToken>()))
             .Returns((int u, int s, int q, CurrencyType c, CancellationToken __) => Task.FromResult(market.Sell(u, s, q, c)));

        var fx = new Mock<IFxRateService>();
        fx.Setup(f => f.GetBidAsk(It.IsAny<CurrencyType>(), It.IsAny<CurrencyType>())).Returns(() => (1.0m, 1.0m));
        fx.Setup(f => f.GetMidRate(It.IsAny<CurrencyType>(), It.IsAny<CurrencyType>())).Returns(1.0m);

        var stocks = new Mock<IStockService>();
        stocks.Setup(s => s.IsListedIn(It.IsAny<int>(), It.IsAny<CurrencyType>())).Returns(true);

        var ctx = new AiBotContext(accounts.Object) { TickId = 1 };
        foreach (var u in usersFactory())
        {
            ctx.AiUsersByAiUserId[u.AiUserId] = u;
            ctx.AiUsersByUserId[u.UserId] = u;
        }

        var economy = new BotEconomyTelemetry(ctx, accounts.Object, fx.Object, NullLogger<BotEconomyTelemetry>.Instance);

        var svc = new ArbitrageDecisionService(entry.Object, books.Object, accounts.Object, fx.Object,
            new Mock<IUserPortfolioService>().Object, stocks.Object, economy,
            NullLogger<ArbitrageDecisionService>.Instance, conversionSkewBand: 0.15m,
            batchLegs: false, sharedScan: sharedScan);

        await svc.RunAsync(ctx, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), CancellationToken.None);
        return (market.Placements, market.Reads);
    }

    // Three arb bots, three cross-listed stocks, all with a USD-buy / EUR-sell gap (eur.Bid > usd.Ask).
    private static FakeMarket ProfitableThree()
    {
        var m = new FakeMarket();
        foreach (var sid in new[] { 101, 102, 103 })
        {
            m.Books[(sid, USD)] = new Level { BidPx = 99m, BidQty = 1000, AskPx = 100m, AskQty = 1000 };
            m.Books[(sid, EUR)] = new Level { BidPx = 110m, BidQty = 1000, AskPx = 112m, AskQty = 1000 };
        }
        return m;
    }

    [Fact]
    public async Task Shared_scan_matches_per_bot_scan_and_reads_less()
    {
        AIUser[] Users() => new[]
        {
            MakeArbUser(1, 1001, new[] { 101, 102, 103 }),
            MakeArbUser(2, 1002, new[] { 101, 102, 103 }),
            MakeArbUser(3, 1003, new[] { 101, 102, 103 }),
        };

        var off = await RunTick(false, ProfitableThree, Users);
        var on  = await RunTick(true,  ProfitableThree, Users);

        Assert.NotEmpty(off.Placements);                        // the cohort actually traded
        Assert.Equal(off.Placements, on.Placements);            // byte-identical placement stream
        Assert.True(on.Reads < off.Reads, $"shared scan should read fewer books: ON={on.Reads} OFF={off.Reads}");
    }

    [Fact]
    public async Task Adversarial_drain_still_matches_per_bot_scan()
    {
        // Stock 200 (X): a DOMINANT gap (rate ~0.30) with a SMALL ask so the leading bot's leg fully drains
        // its USD touch — after which X has no profitable gap. Stock 201 (Y): a tiny gap (rate ~0.02).
        // Leading bot overwhelmingly picks X (weight ∝ rate²) and drains it; the trailing bot must then NOT
        // see X. A naive share-once map keeps X's stale gap ⇒ the trailing bot re-picks the dominant X ⇒
        // stream diverges. The self-invalidating map recomputes X (drained ⇒ gone) and matches OFF.
        FakeMarket Market()
        {
            var m = new FakeMarket();
            m.Books[(200, USD)] = new Level { BidPx = 99m,  BidQty = 1000, AskPx = 100m, AskQty = 100 };  // small ask ⇒ drainable
            m.Books[(200, EUR)] = new Level { BidPx = 130m, BidQty = 1000, AskPx = 132m, AskQty = 1000 }; // profit 30 ⇒ rate 0.30
            m.Books[(201, USD)] = new Level { BidPx = 99m,  BidQty = 1000, AskPx = 100m, AskQty = 1000 };
            m.Books[(201, EUR)] = new Level { BidPx = 102m, BidQty = 1000, AskPx = 104m, AskQty = 1000 }; // profit 2 ⇒ rate 0.02
            return m;
        }
        AIUser[] Users() => new[]
        {
            MakeArbUser(1, 5001, new[] { 200, 201 }),
            MakeArbUser(2, 5002, new[] { 200, 201 }),
        };

        var off = await RunTick(false, Market, Users);
        var on  = await RunTick(true,  Market, Users);

        Assert.NotEmpty(off.Placements);
        Assert.Equal(off.Placements, on.Placements);
        Assert.True(on.Reads < off.Reads, $"shared scan should read fewer books: ON={on.Reads} OFF={off.Reads}");
    }

    [Fact]
    public async Task No_opportunity_tick_is_a_no_op_both_ways()
    {
        // Both books balanced (no cross-book gap) ⇒ no opps, no placements, either way.
        FakeMarket Flat()
        {
            var m = new FakeMarket();
            foreach (var sid in new[] { 300, 301 })
            {
                m.Books[(sid, USD)] = new Level { BidPx = 100m, BidQty = 1000, AskPx = 101m, AskQty = 1000 };
                m.Books[(sid, EUR)] = new Level { BidPx = 100m, BidQty = 1000, AskPx = 101m, AskQty = 1000 };
            }
            return m;
        }
        AIUser[] Users() => new[] { MakeArbUser(1, 7001, new[] { 300, 301 }), MakeArbUser(2, 7002, new[] { 300, 301 }) };

        var off = await RunTick(false, Flat, Users);
        var on  = await RunTick(true,  Flat, Users);

        Assert.Empty(off.Placements);
        Assert.Equal(off.Placements, on.Placements);
    }
}
