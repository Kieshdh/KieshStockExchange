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
/// §A1b golden/equivalence tests: a batched BUY-stop arm
/// (<see cref="OrderExecutionService.ArmStopBuyBatchAsync"/>) must produce the SAME Pending row,
/// the same <c>Fund.ReservedBalance</c>/<c>TotalBalance</c>, the same per-order
/// <c>CurrentBuyReservation</c>, and the same reservation-ledger AMOUNT tuples as the per-order path
/// (<see cref="OrderExecutionService.ArmStopAsync"/> on a stop-limit buy → SettleOrderAsync buy
/// branch). Reason labels differ by design ("ArmBuyBatch:Reserve" vs "SettleOrderAsync:Reserve");
/// amounts must not. This is the CASH/Fund mirror of <see cref="ArmStopBatchEquivalenceTests"/> — the
/// buy batch is fund-GATED (a Fund is per-(user,currency), mutated off-loop under the gate), which is
/// the deliberate CK-safety divergence from the ungated sell batch.
///
/// Coverage note: cases 5 (zero/negative reservation) is a defensive branch not cleanly reachable
/// past OrderValidator; case 9 (flag-off routing to the per-order path) lives in AiTradeService's
/// partition, not the engine, and is proven by the byte-identical OFF soak arm; case 10 (cold-load
/// reload) is not meaningful against a mock that shares the Fund instance. The CK-critical net-new
/// risks — same-user first-touch snapshot (4) and Phase-2 rollback fund-restore (7) — are covered.
/// </summary>
public class ArmStopBuyBatchEquivalenceTests
{
    private const int UserId = 7;
    private const int StockId = 10;

    private sealed record RowSnap(string Status, OrderSide Side, EntryType Entry, StopKind Stop,
        decimal? StopPrice, decimal? TrailOffset, bool? TrailIsPercent, decimal? TrailWatermark,
        decimal? SlippagePercent, int Quantity, decimal Price);

    private static RowSnap Snap(Order o) => new(o.Status, o.Side, o.Entry, o.Stop, o.StopPrice,
        o.TrailOffset, o.TrailIsPercent, o.TrailWatermark, o.SlippagePercent, o.Quantity, o.Price);

    /// <summary>Records fund + order reservation amount tuples; labels kept but compared separately.</summary>
    private sealed class RecordingLedger : IReservationLedger
    {
        public readonly List<(string Action, decimal Amount, decimal ResBefore, decimal ResAfter,
            decimal TotBefore, decimal TotAfter)> FundEntries = new();
        public readonly List<(string Action, decimal Amount, decimal BuyBefore, decimal BuyAfter,
            int SellBefore, int SellAfter)> OrderEntries = new();

        public HashSet<int> TrackedUserIds { get; } = new();
        public bool TrackAll { get; set; }
        public IReadOnlyList<LedgerEntry> Snapshot() => Array.Empty<LedgerEntry>();
        public int EntryCount => 0;
        public string SuggestedExportFileName => "test";
        public void LogFund(int userId, CurrencyType ccy, int? orderId, string action,
            decimal amount, decimal reservedBefore, decimal reservedAfter,
            decimal totalBefore, decimal totalAfter)
            => FundEntries.Add((action, amount, reservedBefore, reservedAfter, totalBefore, totalAfter));
        public void LogPosition(int userId, int stockId, int? orderId, string action,
            decimal amount, int reservedBefore, int reservedAfter,
            int quantityBefore, int quantityAfter) { }
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
        public Fund Fund = null!;
        public RecordingLedger Ledger = null!;
        public readonly List<RowSnap> InsertedRows = new();
        // Fund.ReservedBalance at each UpdateAllAsync<Fund> call (the in-tx persist).
        public readonly List<decimal> UpdatedFundReserved = new();
    }

    private static World NewWorld(decimal fundTotal = 100_000m, bool throwOnInsert = false)
    {
        var w = new World();
        w.Fund = new Fund { UserId = UserId, CurrencyType = CurrencyType.USD, TotalBalance = fundTotal, ReservedBalance = 0m };
        var position = new Position { PositionId = 1, UserId = UserId, StockId = StockId, Quantity = 0 };

        var db = new Mock<IDataBaseService>(MockBehavior.Loose);
        db.Setup(d => d.GetFundsForUsersAsync(It.IsAny<List<int>>(), It.IsAny<CancellationToken>()))
          .ReturnsAsync(new List<Fund> { w.Fund });
        db.Setup(d => d.GetPositionsForUsersAsync(It.IsAny<List<int>>(), It.IsAny<CancellationToken>()))
          .ReturnsAsync(new List<Position> { position });
        db.Setup(d => d.GetOpenOrdersForUsersAsync(It.IsAny<List<int>>(), It.IsAny<CancellationToken>()))
          .ReturnsAsync(new List<Order>());

        int nextId = 1;
        db.Setup(d => d.CreateOrder(It.IsAny<Order>(), It.IsAny<CancellationToken>()))
          .Callback<Order, CancellationToken>((o, _) => { o.OrderId = nextId++; w.InsertedRows.Add(Snap(o)); })
          .Returns(Task.CompletedTask);
        db.Setup(d => d.InsertAllAsync(It.IsAny<IEnumerable<Order>>(), It.IsAny<CancellationToken>()))
          .Callback<IEnumerable<Order>, CancellationToken>((items, _) =>
          {
              if (throwOnInsert) throw new InvalidOperationException("simulated bulk-insert failure");
              foreach (var o in items) { o.OrderId = nextId++; w.InsertedRows.Add(Snap(o)); }
          })
          .Returns(Task.CompletedTask);
        db.Setup(d => d.UpdateAllAsync(It.IsAny<IEnumerable<Fund>>(), It.IsAny<CancellationToken>()))
          .Callback<IEnumerable<Fund>, CancellationToken>((items, _) =>
          { foreach (var f in items) w.UpdatedFundReserved.Add(f.ReservedBalance); })
          .Returns(Task.CompletedTask);
        db.Setup(d => d.UpdateAllAsync(It.IsAny<IEnumerable<Position>>(), It.IsAny<CancellationToken>()))
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

    // A stop-LIMIT buy exactly as OrderEntryService.ArmStopBuyBatchAsync builds it: limit = StopPrice × 1.005.
    private static Order CappedBuyStop(int qty = 10, decimal stopPrice = 105m) => new()
    {
        UserId = UserId, StockId = StockId, Quantity = qty,
        StopPrice = stopPrice,
        Price = CurrencyHelper.RoundMoney(stopPrice * 1.005m, CurrencyType.USD),
        CurrencyType = CurrencyType.USD,
        Side = OrderSide.Buy, Entry = EntryType.Limit, Stop = StopKind.Stop,
    };

    private static Order SellStop(int qty = 10) => new()
    {
        UserId = UserId, StockId = StockId, Quantity = qty,
        Price = 100m, SlippagePercent = 0.3m, StopPrice = 95m,
        CurrencyType = CurrencyType.USD,
        Side = OrderSide.Sell, Entry = EntryType.Market, Stop = StopKind.Stop,
    };

    private static List<(decimal, decimal, decimal, decimal, decimal)> FundTuples(RecordingLedger l)
        => l.FundEntries.Select(e => (e.Amount, e.ResBefore, e.ResAfter, e.TotBefore, e.TotAfter)).ToList();

    private static List<(decimal, decimal, decimal)> OrderTuples(RecordingLedger l)
        => l.OrderEntries.Select(e => (e.Amount, e.BuyBefore, e.BuyAfter)).ToList();

    // 1. Happy equivalence: per-order ArmStopAsync vs the batch → identical row/fund/reservation/amounts.
    [Fact]
    public async Task Batched_buy_stop_matches_per_order_rows_reservations_and_ledger()
    {
        var a = NewWorld();
        var b = NewWorld();

        var a1 = CappedBuyStop();
        var ra1 = await a.Engine.ArmStopAsync(a1);
        Assert.True(ra1.PlacedSuccessfully);
        Assert.Empty(ra1.FillTransactions);

        var b1 = CappedBuyStop();
        var rb = await b.Engine.ArmStopBuyBatchAsync(new[] { b1 });
        Assert.True(rb[0].PlacedSuccessfully);
        Assert.Empty(rb[0].FillTransactions);

        // Identical persisted rows (Status=Pending + every schema column), same shape (buy stop-limit).
        Assert.Equal(a.InsertedRows, b.InsertedRows);
        Assert.All(b.InsertedRows, r => Assert.Equal(Order.Statuses.Pending, r.Status));
        Assert.All(b.InsertedRows, r =>
        {
            Assert.Equal(OrderSide.Buy, r.Side);
            Assert.Equal(EntryType.Limit, r.Entry);
            Assert.Equal(StopKind.Stop, r.Stop);
        });

        // Identical reservation state: fund cash, per-order buy field, and ledger amount tuples
        // (reason labels differ by design — "SettleOrderAsync:Reserve" vs "ArmBuyBatch:Reserve").
        Assert.True(a.Fund.ReservedBalance > 0m);
        Assert.Equal(a.Fund.ReservedBalance, b.Fund.ReservedBalance);
        Assert.Equal(a.Fund.TotalBalance, b.Fund.TotalBalance);
        Assert.Equal(a1.CurrentBuyReservation, b1.CurrentBuyReservation);
        Assert.Equal(FundTuples(a.Ledger), FundTuples(b.Ledger));
        Assert.Equal(OrderTuples(a.Ledger), OrderTuples(b.Ledger));

        // The batch persisted the Fund reservation inside its insert tx.
        Assert.Contains(a.Fund.ReservedBalance, b.UpdatedFundReserved);
    }

    // 2. Insufficient funds in a mixed cohort: survivors reserve, the reject holds nothing.
    [Fact]
    public async Task Insufficient_funds_rejects_individual_and_reserves_only_survivors()
    {
        var w = NewWorld(fundTotal: 8_000m); // covers one ~6.3k buy-stop but not two
        var ok = CappedBuyStop(qty: 60);
        var tooBig = CappedBuyStop(qty: 60);

        var rs = await w.Engine.ArmStopBuyBatchAsync(new[] { ok, tooBig });

        Assert.True(rs[0].PlacedSuccessfully);
        Assert.False(rs[1].PlacedSuccessfully);
        Assert.Contains("Insufficient funds", rs[1].ErrorMessage);
        // Cache reservation is exactly the survivor's hold; only it was inserted; the reject holds nothing.
        Assert.Equal(ok.CurrentBuyReservation, w.Fund.ReservedBalance);
        Assert.Single(w.InsertedRows);
        Assert.Equal(0m, tooBig.CurrentBuyReservation);
    }

    // 3. Wrong side: a sell-stop in the buy batch → guard message, zero mutation.
    [Fact]
    public async Task Sell_stop_is_rejected_with_invalid_params_and_no_mutation()
    {
        var w = NewWorld();
        var rs = await w.Engine.ArmStopBuyBatchAsync(new[] { SellStop() });

        Assert.False(rs[0].PlacedSuccessfully);
        Assert.Contains("buy-stops only", rs[0].ErrorMessage);
        Assert.Empty(w.InsertedRows);
        Assert.Equal(0m, w.Fund.ReservedBalance);
    }

    // 4. Same user, multiple buy-stops in one tick → reservations accumulate on the one fund.
    [Fact]
    public async Task Same_user_multiple_buy_stops_accumulate_reservations()
    {
        var w = NewWorld();
        var s1 = CappedBuyStop(qty: 10, stopPrice: 105m);
        var s2 = CappedBuyStop(qty: 20, stopPrice: 110m);

        var rs = await w.Engine.ArmStopBuyBatchAsync(new[] { s1, s2 });

        Assert.True(rs[0].PlacedSuccessfully);
        Assert.True(rs[1].PlacedSuccessfully);
        Assert.Equal(s1.CurrentBuyReservation + s2.CurrentBuyReservation, w.Fund.ReservedBalance);
        Assert.Equal(2, w.InsertedRows.Count);
    }

    // 6. Empty cohort → empty result, no tx, no reserve.
    [Fact]
    public async Task Empty_cohort_returns_empty_and_reserves_nothing()
    {
        var w = NewWorld();
        var rs = await w.Engine.ArmStopBuyBatchAsync(Array.Empty<Order>());
        Assert.Empty(rs);
        Assert.Empty(w.InsertedRows);
        Assert.Equal(0m, w.Fund.ReservedBalance);
    }

    // 7. Phase-2 bulk-insert failure with a SAME-USER cohort → every touched fund restored to the
    // PRE-COHORT value. This is the first-touch-snapshot regression: if the snapshot were taken per
    // reserve (not first-touch), s2's reserve would overwrite s1's pre-cohort snapshot and the
    // rollback would leak s1's hold. It must land back at exactly 0.
    [Fact]
    public async Task Bulk_insert_failure_restores_all_touched_funds_to_pre_cohort()
    {
        var w = NewWorld(throwOnInsert: true);
        var s1 = CappedBuyStop(qty: 10);
        var s2 = CappedBuyStop(qty: 20);

        var rs = await w.Engine.ArmStopBuyBatchAsync(new[] { s1, s2 });

        Assert.All(rs, r => Assert.False(r.PlacedSuccessfully));
        Assert.All(rs, r => Assert.Contains("Bulk arm insert failed", r.ErrorMessage));
        Assert.Equal(0m, w.Fund.ReservedBalance); // restored to pre-cohort
        Assert.Empty(w.InsertedRows);
    }

    // 8. A downward StopPrice (direction-sanity is the entry layer's job) must be handled IDENTICALLY
    // by the per-order and batch engine paths — no divergent accept/leak between the two routes.
    [Fact]
    public async Task Downward_stop_price_is_handled_identically_by_both_paths()
    {
        var a = NewWorld();
        var b = NewWorld();
        var oa = CappedBuyStop(qty: 10, stopPrice: 90m);
        var ob = CappedBuyStop(qty: 10, stopPrice: 90m);

        var ra = await a.Engine.ArmStopAsync(oa);
        var rb = await b.Engine.ArmStopBuyBatchAsync(new[] { ob });

        Assert.Equal(ra.PlacedSuccessfully, rb[0].PlacedSuccessfully);
        Assert.Equal(a.Fund.ReservedBalance, b.Fund.ReservedBalance);
        Assert.Equal(oa.CurrentBuyReservation, ob.CurrentBuyReservation);
    }
}
