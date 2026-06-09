using KieshStockExchange.Helpers;
using KieshStockExchange.Models;
using KieshStockExchange.Server.Services.OtherServices;
using KieshStockExchange.Services.DataServices;
using KieshStockExchange.Services.DataServices.Interfaces;
using KieshStockExchange.Services.MarketDataServices.Interfaces;
using KieshStockExchange.Services.MarketEngineServices;
using KieshStockExchange.Services.MarketEngineServices.Interfaces;
using KieshStockExchange.Services.PortfolioServices;
using KieshStockExchange.Services.PortfolioServices.Helpers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace KieshStockExchange.Tests;

/// <summary>
/// §A1a golden/equivalence tests: a batched arm (<see cref="OrderExecutionService.ArmStopBatchAsync"/>)
/// must produce the SAME Pending row, the same Position.ReservedQuantity, the same per-order
/// reservation, and the same reservation-ledger amount tuples as the per-order
/// <see cref="OrderExecutionService.ArmStopAsync"/> — reason labels differ by design
/// ("ArmBatch:Reserve" vs "SettleOrderAsync:Reserve"), amounts must not. Two independent
/// worlds (own cache/registry/db) run the same orders through each path and are diffed.
/// </summary>
public class ArmStopBatchEquivalenceTests
{
    private const int UserId = 7;
    private const int StockId = 10;

    // The stop-schema columns an armed Pending row must round-trip identically (OrderId excluded:
    // both worlds assign 1,2,... independently).
    private sealed record RowSnap(string Status, OrderSide Side, EntryType Entry, StopKind Stop,
        decimal? StopPrice, decimal? TrailOffset, bool? TrailIsPercent, decimal? TrailWatermark,
        decimal? SlippagePercent, int Quantity, decimal Price);

    private static RowSnap Snap(Order o) => new(o.Status, o.Side, o.Entry, o.Stop, o.StopPrice,
        o.TrailOffset, o.TrailIsPercent, o.TrailWatermark, o.SlippagePercent, o.Quantity, o.Price);

    /// <summary>Ledger that records amount tuples; labels are kept but compared separately.</summary>
    private sealed class RecordingLedger : IReservationLedger
    {
        public readonly List<(string Action, decimal Amount, decimal Before, decimal After)> PositionEntries = new();
        public readonly List<(string Action, decimal Amount, decimal BuyBefore, decimal BuyAfter,
            int SellBefore, int SellAfter)> OrderEntries = new();

        public HashSet<int> TrackedUserIds { get; } = new();
        public bool TrackAll { get; set; }
        public IReadOnlyList<LedgerEntry> Snapshot() => Array.Empty<LedgerEntry>();
        public int EntryCount => 0;
        public string SuggestedExportFileName => "test";
        public void LogFund(int userId, CurrencyType ccy, int? orderId, string action,
            decimal amount, decimal reservedBefore, decimal reservedAfter,
            decimal totalBefore, decimal totalAfter) { }
        public void LogPosition(int userId, int stockId, int? orderId, string action,
            decimal amount, int reservedBefore, int reservedAfter,
            int quantityBefore, int quantityAfter)
            => PositionEntries.Add((action, amount, reservedBefore, reservedAfter));
        public void LogOrder(int userId, int orderId, string action, decimal amount,
            decimal buyReservationBefore, decimal buyReservationAfter,
            int sellReservedBefore, int sellReservedAfter)
            => OrderEntries.Add((action, amount, buyReservationBefore, buyReservationAfter,
                sellReservedBefore, sellReservedAfter));
        public void LogTransaction(int buyerId, int sellerId, int stockId, CurrencyType ccy,
            int buyOrderId, int sellOrderId, int quantity, decimal price, decimal totalAmount) { }
        public Task<string> ExportCsvAsync(string path, CancellationToken ct = default) => Task.FromResult(path);
        public string BuildCsv(CancellationToken ct = default) => string.Empty;
        public void Clear() { }
    }

    /// <summary>One isolated engine stack: capturing db mock + real cache/registry/settlement.</summary>
    private sealed class World
    {
        public OrderExecutionService Engine = null!;
        public AccountsCache Accounts = null!;
        public Position Position = null!;
        public RecordingLedger Ledger = null!;
        // Every Order row persisted (CreateOrder or bulk InsertAllAsync), snapshotted at insert time.
        public readonly List<RowSnap> InsertedRows = new();
        // Position.ReservedQuantity at each UpdateAllAsync<Position> call (the in-tx persist).
        public readonly List<int> UpdatedPositionReserved = new();
    }

    private static World NewWorld(int positionQty = 100)
    {
        var w = new World();
        w.Position = new Position { PositionId = 1, UserId = UserId, StockId = StockId, Quantity = positionQty };
        var fund = new Fund { UserId = UserId, CurrencyType = CurrencyType.USD, TotalBalance = 10_000m, ReservedBalance = 0m };

        var db = new Mock<IDataBaseService>(MockBehavior.Loose);
        db.Setup(d => d.GetFundsForUsersAsync(It.IsAny<List<int>>(), It.IsAny<CancellationToken>()))
          .ReturnsAsync(new List<Fund> { fund });
        db.Setup(d => d.GetPositionsForUsersAsync(It.IsAny<List<int>>(), It.IsAny<CancellationToken>()))
          .ReturnsAsync(new List<Position> { w.Position });
        db.Setup(d => d.GetOpenOrdersForUsersAsync(It.IsAny<List<int>>(), It.IsAny<CancellationToken>()))
          .ReturnsAsync(new List<Order>());

        int nextId = 1;
        db.Setup(d => d.CreateOrder(It.IsAny<Order>(), It.IsAny<CancellationToken>()))
          .Callback<Order, CancellationToken>((o, _) => { o.OrderId = nextId++; w.InsertedRows.Add(Snap(o)); })
          .Returns(Task.CompletedTask);
        db.Setup(d => d.InsertAllAsync(It.IsAny<IEnumerable<Order>>(), It.IsAny<CancellationToken>()))
          .Callback<IEnumerable<Order>, CancellationToken>((items, _) =>
          { foreach (var o in items) { o.OrderId = nextId++; w.InsertedRows.Add(Snap(o)); } })
          .Returns(Task.CompletedTask);
        db.Setup(d => d.UpdateAllAsync(It.IsAny<IEnumerable<Position>>(), It.IsAny<CancellationToken>()))
          .Callback<IEnumerable<Position>, CancellationToken>((items, _) =>
          { foreach (var p in items) w.UpdatedPositionReserved.Add(p.ReservedQuantity); })
          .Returns(Task.CompletedTask);
        db.Setup(d => d.BeginTransactionAsync(It.IsAny<CancellationToken>()))
          .ReturnsAsync(new Mock<ITransaction>().Object);

        var registry = new OrderRegistry();
        w.Ledger = new RecordingLedger();
        w.Accounts = new AccountsCache(db.Object, registry, w.Ledger, NullLogger<AccountsCache>.Instance);

        var stocks = new Mock<IStockService>();
        Stock? stockOut = new Stock();
        stocks.Setup(s => s.TryGetById(It.IsAny<int>(), out stockOut)).Returns(true);
        stocks.Setup(s => s.IsListedIn(It.IsAny<int>(), It.IsAny<CurrencyType>())).Returns(true);
        var validator = new OrderValidator(stocks.Object);

        var settlement = new SettlementEngine(db.Object, w.Accounts, w.Ledger, registry,
            NullLogger<SettlementEngine>.Instance, NullLoggerFactory.Instance,
            Options.Create(new SeparatorLoggerOptions()));

        // GetValue("Db:MaxConcurrentGroups", 24) walks GetSection(...).Value → null → default.
        var config = new Mock<IConfiguration>();
        config.Setup(c => c.GetSection(It.IsAny<string>())).Returns(Mock.Of<IConfigurationSection>());

        w.Engine = new OrderExecutionService(
            db.Object,
            new Mock<IOrderBookEngine>().Object,
            new Mock<IMatchingEngine>().Object,
            validator,
            settlement,
            new Mock<IMarketDataService>().Object,
            w.Accounts,
            new Mock<IOrderCacheService>().Object,
            w.Ledger,
            registry,
            new Mock<IServerNotificationService>().Object,
            new Mock<IBracketCoordinator>().Object,
            config.Object,
            NullLogger<OrderExecutionService>.Instance);
        return w;
    }

    // The two bot arm shapes, exactly as the OrderEntryService builders emit them: a
    // slippage-capped stop-market sell (anchor Price = arm-time market) and a trailing sell
    // (Price 0, watermark-seeded trigger).
    private static Order CappedSellStop(int qty = 10) => new()
    {
        UserId = UserId, StockId = StockId, Quantity = qty,
        Price = 100m, SlippagePercent = 0.3m, StopPrice = 95m,
        CurrencyType = CurrencyType.USD,
        Side = OrderSide.Sell, Entry = EntryType.Market, Stop = StopKind.Stop,
    };

    private static Order TrailingSellStop(int qty = 5) => new()
    {
        UserId = UserId, StockId = StockId, Quantity = qty,
        Price = 0m, StopPrice = 97m,
        TrailOffset = 3m, TrailIsPercent = false, TrailWatermark = 100m,
        CurrencyType = CurrencyType.USD,
        Side = OrderSide.Sell, Entry = EntryType.Market, Stop = StopKind.Trailing,
    };

    private static List<(decimal, decimal, decimal)> PosTuples(RecordingLedger l)
        => l.PositionEntries.Select(e => (e.Amount, e.Before, e.After)).ToList();

    private static List<(decimal, decimal, decimal, decimal, decimal)> OrderTuples(RecordingLedger l)
        => l.OrderEntries.Select(e => (e.Amount, e.BuyBefore, e.BuyAfter,
            (decimal)e.SellBefore, (decimal)e.SellAfter)).ToList();

    [Fact]
    public async Task Batched_arm_matches_per_order_arm_rows_reservations_and_ledger()
    {
        var a = NewWorld();
        var b = NewWorld();

        // World A: per-order ArmStopAsync (the proven path).
        var a1 = CappedSellStop();
        var a2 = TrailingSellStop();
        var ra1 = await a.Engine.ArmStopAsync(a1);
        var ra2 = await a.Engine.ArmStopAsync(a2);
        Assert.True(ra1.PlacedSuccessfully);
        Assert.True(ra2.PlacedSuccessfully);
        Assert.Empty(ra1.FillTransactions);
        Assert.Empty(ra2.FillTransactions);

        // World B: the same two orders through the batch.
        var b1 = CappedSellStop();
        var b2 = TrailingSellStop();
        var rb = await b.Engine.ArmStopBatchAsync(new[] { b1, b2 });
        Assert.True(rb[0].PlacedSuccessfully);
        Assert.True(rb[1].PlacedSuccessfully);
        Assert.Empty(rb[0].FillTransactions);
        Assert.Empty(rb[1].FillTransactions);

        // Identical persisted rows (Status=Pending + every stop-schema column), in the same order.
        Assert.Equal(a.InsertedRows, b.InsertedRows);
        Assert.All(b.InsertedRows, r => Assert.Equal(Order.Statuses.Pending, r.Status));

        // Identical reservation state: cache position, per-order field, and ledger amount tuples
        // (reason labels differ by design — "SettleOrderAsync:Reserve" vs "ArmBatch:Reserve").
        Assert.Equal(15, a.Position.ReservedQuantity);
        Assert.Equal(a.Position.ReservedQuantity, b.Position.ReservedQuantity);
        Assert.Equal(a1.CurrentSellReservedQty, b1.CurrentSellReservedQty);
        Assert.Equal(a2.CurrentSellReservedQty, b2.CurrentSellReservedQty);
        Assert.Equal(PosTuples(a.Ledger), PosTuples(b.Ledger));
        Assert.Equal(OrderTuples(a.Ledger), OrderTuples(b.Ledger));

        // The batch persisted the Position INSIDE its insert tx, landing on the same final
        // ReservedQuantity the per-order path persisted on its last arm.
        Assert.Contains(15, b.UpdatedPositionReserved);
        Assert.Equal(a.UpdatedPositionReserved.Last(), b.UpdatedPositionReserved.Last());
    }

    [Fact]
    public async Task Partial_failure_rejects_individual_and_keeps_the_rest()
    {
        var w = NewWorld(positionQty: 100);
        var ok1 = CappedSellStop(10);
        var bad = CappedSellStop(1000); // > AvailableQuantity → must be rejected, not reserved
        var ok2 = CappedSellStop(5);

        var rs = await w.Engine.ArmStopBatchAsync(new[] { ok1, bad, ok2 });

        Assert.True(rs[0].PlacedSuccessfully);
        Assert.False(rs[1].PlacedSuccessfully);
        Assert.Contains("Insufficient shares", rs[1].ErrorMessage);
        Assert.True(rs[2].PlacedSuccessfully);

        // The reject consumed nothing: cache reservation is exactly the survivors' sum, only
        // the two survivors were inserted, and the reject holds no per-order reservation.
        Assert.Equal(15, w.Position.ReservedQuantity);
        Assert.Equal(2, w.InsertedRows.Count);
        Assert.Equal(0, bad.CurrentSellReservedQty);
    }

    [Fact]
    public async Task Buy_stops_are_rejected_with_invalid_params()
    {
        var w = NewWorld();
        var buyStop = new Order
        {
            UserId = UserId, StockId = StockId, Quantity = 10,
            Price = 0m, StopPrice = 105m, BuyBudget = 1_500m,
            CurrencyType = CurrencyType.USD,
            Side = OrderSide.Buy, Entry = EntryType.Market, Stop = StopKind.Stop,
        };

        var rs = await w.Engine.ArmStopBatchAsync(new[] { buyStop });

        Assert.False(rs[0].PlacedSuccessfully);
        Assert.Contains("sell-stops only", rs[0].ErrorMessage);
        Assert.Empty(w.InsertedRows);
        Assert.Equal(0, w.Position.ReservedQuantity);
    }
}
