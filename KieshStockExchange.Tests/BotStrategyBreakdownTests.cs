using KieshStockExchange.Helpers;
using KieshStockExchange.Models;
using KieshStockExchange.Server.Controllers;
using KieshStockExchange.Services.BackgroundServices.Helpers;
using KieshStockExchange.Services.BackgroundServices.Interfaces;
using KieshStockExchange.Services.DataServices.Interfaces;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace KieshStockExchange.Tests;

/// <summary>
/// §dashboard: tests for the bot-types breakdown — the static per-strategy metadata (exhaustive + valid groups)
/// and the <see cref="BotTelemetryCache.GetStrategyBreakdownAsync"/> aggregation (snapshot columns merged with
/// per-strategy transaction flow; "All" range skips the transaction scan).
/// </summary>
public class BotStrategyBreakdownTests
{
    // Every strategy the fleet can run MUST have a display mapping with a valid dashboard group, so a newly
    // added AiStrategy can never silently fall out of the two-group layout.
    [Fact]
    public void BotStrategyMeta_covers_every_strategy_with_a_valid_group()
    {
        foreach (AiStrategy s in Enum.GetValues<AiStrategy>())
        {
            var (name, group, desc) = BotStrategyMeta.For(s);
            Assert.False(string.IsNullOrWhiteSpace(name), $"{s} has no display name");
            Assert.False(string.IsNullOrWhiteSpace(desc), $"{s} has no description");
            Assert.True(group == BotStrategyMeta.TradersGroup || group == BotStrategyMeta.HouseGroup,
                $"{s} has an unexpected group '{group}'");
        }
    }

    [Fact]
    public void BotStrategyMeta_groups_traders_and_house_as_decided()
    {
        Assert.Equal(BotStrategyMeta.TradersGroup, BotStrategyMeta.For(AiStrategy.Conviction).Group);
        Assert.Equal(BotStrategyMeta.TradersGroup, BotStrategyMeta.For(AiStrategy.TrendFollower).Group);
        // Random is the noise floor and Rotator the correlation balancer — both liquidity, not traders.
        Assert.Equal(BotStrategyMeta.HouseGroup, BotStrategyMeta.For(AiStrategy.Random).Group);
        Assert.Equal(BotStrategyMeta.HouseGroup, BotStrategyMeta.For(AiStrategy.Rotator).Group);
        Assert.Equal(BotStrategyMeta.HouseGroup, BotStrategyMeta.For(AiStrategy.MarketMaker).Group);
    }

    private static Transaction Tx(int buyer, int seller, decimal price, int qty) =>
        new() { StockId = 1, BuyerId = buyer, SellerId = seller, Price = price, Quantity = qty, CurrencyType = CurrencyType.USD };

    private static BotTelemetryCache BuildCache(
        IReadOnlyList<StrategySnapshotRow> snapshot,
        IReadOnlyDictionary<int, AiStrategy> stratMap,
        List<Transaction> txs,
        out Mock<IDataBaseService> db)
    {
        var bots = new Mock<IAiTradeService>();
        bots.Setup(b => b.GetStrategySnapshot()).Returns(snapshot);
        bots.Setup(b => b.GetBotStrategies()).Returns(stratMap);

        db = new Mock<IDataBaseService>();
        db.Setup(d => d.GetTransactionsSinceTime(It.IsAny<DateTime>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()))
          .ReturnsAsync(txs);

        return new BotTelemetryCache(db.Object, bots.Object, NullLogger<BotTelemetryCache>.Instance);
    }

    [Fact]
    public async Task GetStrategyBreakdown_merges_snapshot_with_windowed_flow()
    {
        // 2 Conviction bots (1 winner, +5% P&L, 100 session trades) + 3 Rotator bots (0 winners).
        var snapshot = new List<StrategySnapshotRow>
        {
            new(AiStrategy.Conviction, BotCount: 2, Wins: 1, SessionTrades: 100, CurUsd: 210_000m, SeedUsd: 200_000m),
            new(AiStrategy.Rotator,    BotCount: 3, Wins: 0, SessionTrades: 50,  CurUsd: 90_000m,  SeedUsd: 100_000m),
        };
        var stratMap = new Dictionary<int, AiStrategy>
        {
            [101] = AiStrategy.Conviction, [102] = AiStrategy.Conviction,
            [201] = AiStrategy.Rotator, [202] = AiStrategy.Rotator, [203] = AiStrategy.Rotator,
        };
        var txs = new List<Transaction>
        {
            Tx(buyer: 101, seller: 201, price: 10m, qty: 5),  // Cnv +1/+50, Rot +1/+50
            Tx(buyer: 202, seller: 102, price: 10m, qty: 3),  // Rot +1/+30, Cnv +1/+30
            Tx(buyer: 101, seller: 999, price: 10m, qty: 2),  // Cnv +1/+20 (seller is a human, not in the map)
        };

        var cache = BuildCache(snapshot, stratMap, txs, out _);
        var result = await cache.GetStrategyBreakdownAsync(rangeMinutes: 60, default);

        Assert.Equal(5, result.TotalBots);
        Assert.Equal(5, result.TotalRangeTrades); // 3 Cnv + 2 Rot
        Assert.False(result.RangeCapped);

        var cnv = result.Strategies.Single(r => r.Strategy == (int)AiStrategy.Conviction);
        Assert.Equal(BotStrategyMeta.TradersGroup, cnv.Group);
        Assert.Equal(2, cnv.BotCount);
        Assert.Equal(40.0, cnv.BotSharePercent, 3);   // 2 of 5
        Assert.Equal(50.0, cnv.WinRatePercent, 3);    // 1 of 2
        Assert.Equal(5.0, cnv.PnlPercent, 3);         // (210k-200k)/200k
        Assert.Equal(100, cnv.SessionTrades);
        Assert.Equal(3, cnv.RangeTrades);
        Assert.Equal(100m, cnv.RangeVolume);          // 50 + 30 + 20
        Assert.Equal(1.5, cnv.AvgRangeTradesPerBot, 3);

        var rot = result.Strategies.Single(r => r.Strategy == (int)AiStrategy.Rotator);
        Assert.Equal(BotStrategyMeta.HouseGroup, rot.Group);
        Assert.Equal(2, rot.RangeTrades);
        Assert.Equal(80m, rot.RangeVolume);           // 50 + 30
        Assert.Equal(-10.0, rot.PnlPercent, 3);       // (90k-100k)/100k

        // Default sort is bot-count descending: Rotator (3) before Conviction (2).
        Assert.Equal((int)AiStrategy.Rotator, result.Strategies[0].Strategy);
    }

    [Fact]
    public async Task GetStrategyBreakdown_all_range_skips_transaction_scan_and_zeroes_flow()
    {
        var snapshot = new List<StrategySnapshotRow>
        {
            new(AiStrategy.Conviction, BotCount: 2, Wins: 1, SessionTrades: 100, CurUsd: 210_000m, SeedUsd: 200_000m),
        };
        var cache = BuildCache(snapshot, new Dictionary<int, AiStrategy>(), new List<Transaction>(), out var db);

        var result = await cache.GetStrategyBreakdownAsync(rangeMinutes: 0, default);

        // "All" reads only the cumulative session snapshot — no transaction window scan.
        db.Verify(d => d.GetTransactionsSinceTime(It.IsAny<DateTime>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()), Times.Never);
        var cnv = Assert.Single(result.Strategies);
        Assert.Equal(0, cnv.RangeTrades);
        Assert.Equal(0m, cnv.RangeVolume);
        Assert.Equal(100, cnv.SessionTrades);
    }
}
