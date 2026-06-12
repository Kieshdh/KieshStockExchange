using KieshStockExchange.Helpers;
using KieshStockExchange.Models;
using KieshStockExchange.Server.Services.OtherServices;
using KieshStockExchange.Services.DataServices;
using KieshStockExchange.Services.DataServices.Interfaces;
using KieshStockExchange.Services.MarketEngineServices;
using KieshStockExchange.Services.MarketEngineServices.Interfaces;
using KieshStockExchange.Services.PortfolioServices;
using KieshStockExchange.Services.PortfolioServices.Helpers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace KieshStockExchange.Tests;

/// <summary>
/// R4 §0008.1 — flip-path tests with explicit invariant checks on collateral,
/// CurrentSellReservedQty, and Fund.ReservedBalance. Extends the §0002 harness
/// (<see cref="FlipBatchInterleavingTests"/>) with scenarios that exercise the real
/// short-open branch in detail — §0002 mostly proves NoopShortPart; this file proves the
/// inverse (real collateral reservation) plus rollback symmetry under §0001.
///
/// Lesson #6 from §0002 land: AccountsCache.EnsureLoadedAsync zeros mock-test cache
/// reservations when the OrderRegistry is empty. Each test here restores the seeded
/// Fund.ReservedBalance + Position.ReservedQuantity AFTER EnsureLoadedAsync runs.
/// </summary>
public class BracketMixedPortionSettlementTests
{
    private const int Seller = 7;
    private const int Buyer = 9;
    private const int StockId = 10;
    private const CurrencyType Ccy = CurrencyType.USD;

    private sealed class RecordingLedger : IReservationLedger
    {
        public readonly List<(string Action, decimal Amount)> PositionEntries = new();
        public readonly List<(string Action, decimal Amount, decimal Before, decimal After)> FundEntries = new();
        public HashSet<int> TrackedUserIds { get; } = new();
        public bool TrackAll { get; set; }
        public IReadOnlyList<LedgerEntry> Snapshot() => Array.Empty<LedgerEntry>();
        public int EntryCount => 0;
        public string SuggestedExportFileName => "test";
        public void LogFund(int userId, CurrencyType ccy, int? orderId, string action,
            decimal amount, decimal reservedBefore, decimal reservedAfter,
            decimal totalBefore, decimal totalAfter)
            => FundEntries.Add((action, amount, reservedBefore, reservedAfter));
        public void LogPosition(int userId, int stockId, int? orderId, string action,
            decimal amount, int reservedBefore, int reservedAfter,
            int quantityBefore, int quantityAfter)
            => PositionEntries.Add((action, amount));
        public void LogOrder(int userId, int orderId, string action, decimal amount,
            decimal buyReservationBefore, decimal buyReservationAfter,
            int sellReservedBefore, int sellReservedAfter) { }
        public void LogTransaction(int buyerId, int sellerId, int stockId, CurrencyType ccy,
            int buyOrderId, int sellOrderId, int quantity, decimal price, decimal totalAmount) { }
        public Task<string> ExportCsvAsync(string path, CancellationToken ct = default) => Task.FromResult(path);
        public string BuildCsv(CancellationToken ct = default) => string.Empty;
        public void Clear() { }
    }

    private sealed class World
    {
        public AccountsCache Accounts = null!;
        public SettlementEngine Settlement = null!;
        public RecordingLedger Ledger = null!;
        public Mock<IDataBaseService> Db = null!;
        public Position SellerPos = null!;
        public Fund SellerFund = null!;
        public Fund BuyerFund = null!;
        // Seeded reservations preserved through EnsureLoadedAsync zero-out.
        public decimal SellerFundReservedSeed;
        public int SellerPosReservedSeed;
        public decimal BuyerFundReservedSeed;
    }

    /// <summary>
    /// Build a world with the given seller position state. Caller calls
    /// <see cref="RestoreSeededReservations"/> after any explicit
    /// <c>EnsureLoadedAsync</c> to compensate for the cache backfill that zeros
    /// ReservedBalance / ReservedQuantity when the OrderRegistry is empty
    /// (lesson #6 from §0002 land).
    /// </summary>
    private static World NewWorld(int sellerStartQty, int sellerReservedQty,
        decimal sellerFundReserved = 0m, decimal buyerFundReserved = 500m)
    {
        var w = new World
        {
            SellerPos = new Position
            {
                PositionId = 1, UserId = Seller, StockId = StockId,
                Quantity = sellerStartQty, ReservedQuantity = sellerReservedQty,
            },
            SellerFund = new Fund
            {
                UserId = Seller, CurrencyType = Ccy,
                TotalBalance = 5_000m, ReservedBalance = sellerFundReserved,
            },
            BuyerFund = new Fund
            {
                UserId = Buyer, CurrencyType = Ccy,
                TotalBalance = 5_000m, ReservedBalance = buyerFundReserved,
            },
            SellerFundReservedSeed = sellerFundReserved,
            SellerPosReservedSeed = sellerReservedQty,
            BuyerFundReservedSeed = buyerFundReserved,
        };

        w.Db = new Mock<IDataBaseService>(MockBehavior.Loose);
        w.Db.Setup(d => d.GetFundsForUsersAsync(It.IsAny<List<int>>(), It.IsAny<CancellationToken>()))
             .ReturnsAsync(new List<Fund> { w.SellerFund, w.BuyerFund });
        w.Db.Setup(d => d.GetPositionsForUsersAsync(It.IsAny<List<int>>(), It.IsAny<CancellationToken>()))
             .ReturnsAsync(new List<Position> { w.SellerPos });
        w.Db.Setup(d => d.GetOpenOrdersForUsersAsync(It.IsAny<List<int>>(), It.IsAny<CancellationToken>()))
             .ReturnsAsync(new List<Order>());
        w.Db.Setup(d => d.BeginTransactionAsync(It.IsAny<CancellationToken>()))
             .ReturnsAsync(new Mock<ITransaction>().Object);

        var registry = new OrderRegistry();
        w.Ledger = new RecordingLedger();
        w.Accounts = new AccountsCache(w.Db.Object, registry, w.Ledger, NullLogger<AccountsCache>.Instance);
        w.Settlement = new SettlementEngine(w.Db.Object, w.Accounts, w.Ledger, registry,
            NullLogger<SettlementEngine>.Instance, NullLoggerFactory.Instance,
            Options.Create(new SeparatorLoggerOptions()));
        return w;
    }

    private static void RestoreSeededReservations(World w)
    {
        w.SellerFund.ReservedBalance = w.SellerFundReservedSeed;
        w.SellerPos.ReservedQuantity = w.SellerPosReservedSeed;
        w.BuyerFund.ReservedBalance = w.BuyerFundReservedSeed;
    }

    private static Order ShortBracketSell(int qty, int reservedQty, int flipQty, int orderId = 100)
    {
        var o = new Order
        {
            OrderId = orderId, UserId = Seller, StockId = StockId, CurrencyType = Ccy,
            Quantity = qty, Price = 10m, SlippagePercent = 0.5m,
            Side = OrderSide.Sell, Entry = EntryType.Market, Stop = StopKind.None,
            FlipQuantity = flipQty,
        };
        if (reservedQty > 0) o.TakeSellReservation(reservedQty);
        return o;
    }

    private static Order BuyerCounterparty(int qty, decimal price, int orderId = 200)
    {
        var o = new Order
        {
            OrderId = orderId, UserId = Buyer, StockId = StockId, CurrencyType = Ccy,
            Quantity = qty, Price = price,
            Side = OrderSide.Buy, Entry = EntryType.Limit, Stop = StopKind.None,
        };
        o.TakeBuyReservation(qty * price);
        return o;
    }

    [Fact]
    public async Task Real_flip_reserves_collateral_and_drains_long_pool_in_lock_step()
    {
        // Setup: pre-batch Q=5, R=5 (all 5 shares reserved as the SellOrder's long pool).
        // FlipQuantity=5, sell qty=10. Trade fully fills:
        //   longPart  = min(10, 5) = 5  → ConsumeReservedStock(5): Q 5→0, R 5→0;
        //                                  ConsumeSellReservation(5): order R 5→0
        //   shortPart = 10 - 5     = 5  → ApplyDelta(-5):           Q 0→-5
        //                                  Q < 0 ⇒ real short open
        //                                  collateral = 5*10 = 50
        //                                  reserved on Fund (market-order branch at TradeSettler:466)
        var w = NewWorld(sellerStartQty: 5, sellerReservedQty: 5);
        var sellOrder = ShortBracketSell(qty: 10, reservedQty: 5, flipQty: 5);
        var buyOrder = BuyerCounterparty(qty: 10, price: 10m);
        var ordersById = new Dictionary<int, Order> { [sellOrder.OrderId] = sellOrder, [buyOrder.OrderId] = buyOrder };

        await w.Accounts.EnsureLoadedAsync(new List<int> { Seller, Buyer }, default);
        RestoreSeededReservations(w);

        var sellerFundReservedBefore = w.SellerFund.ReservedBalance;

        var tx = new Transaction
        {
            StockId = StockId, CurrencyType = Ccy, Quantity = 10, Price = 10m,
            BuyerId = Buyer, SellerId = Seller,
            BuyOrderId = buyOrder.OrderId, SellOrderId = sellOrder.OrderId,
        };
        var scope = new TradeBatchScope();

        var (err, _) = await w.Settlement.SettleTradesNoTxAsync(
            new[] { tx }, ordersById, scope, default);

        Assert.Null(err);

        // Position invariants
        Assert.Equal(-5, w.SellerPos.Quantity);
        Assert.Equal(0, w.SellerPos.ReservedQuantity);
        Assert.Equal(50m, w.SellerPos.ShortCollateral);
        Assert.Equal(Ccy, w.SellerPos.ShortCollateralCurrency);

        // Order invariants: long pool fully drained.
        Assert.Equal(0, sellOrder.CurrentSellReservedQty);

        // Fund invariants: ReservedBalance went up by exactly the collateral amount
        // (the market-flip short reserves at fill).
        Assert.Equal(sellerFundReservedBefore + 50m, w.SellerFund.ReservedBalance);

        // Ledger invariants: both flip half-events fired, NoopShortPart did not.
        Assert.Contains(w.Ledger.PositionEntries, e => e.Action == "ApplyPass:Flip:ConsumeReservedStock");
        Assert.Contains(w.Ledger.PositionEntries, e => e.Action == "ApplyPass:Flip:ShortOpen");
        Assert.DoesNotContain(w.Ledger.PositionEntries, e => e.Action == "ApplyPass:Flip:NoopShortPart");

        // Fund ledger: ApplyPass:Flip:ShortOpen:ReserveCollateral reserved exactly 50.
        Assert.Contains(w.Ledger.FundEntries,
            e => e.Action == "ApplyPass:Flip:ShortOpen:ReserveCollateral" && e.Amount == 50m);
    }

    [Fact]
    public async Task Pure_short_open_flat_seller_reserves_proceeds_as_collateral()
    {
        // Negative-control variant of the §0002 / §F14 short-open path. Pre-batch Q=0
        // (flat seller). SellOrder is a market sell with no FlipQuantity — sellHasShortPart
        // is true (IsMarketOrder), sellerStartQty == 0 ⇒ isShortFill branch (not isFlipFill).
        // Expect: ApplyDelta(-10) → Q=-10, collateral = 10*10 = 100, Fund.ReservedBalance += 100.
        var w = NewWorld(sellerStartQty: 0, sellerReservedQty: 0);
        var sellOrder = ShortBracketSell(qty: 10, reservedQty: 0, flipQty: 0);
        var buyOrder = BuyerCounterparty(qty: 10, price: 10m, orderId: 201);
        var ordersById = new Dictionary<int, Order> { [sellOrder.OrderId] = sellOrder, [buyOrder.OrderId] = buyOrder };

        await w.Accounts.EnsureLoadedAsync(new List<int> { Seller, Buyer }, default);
        RestoreSeededReservations(w);

        var sellerFundReservedBefore = w.SellerFund.ReservedBalance;

        var tx = new Transaction
        {
            StockId = StockId, CurrencyType = Ccy, Quantity = 10, Price = 10m,
            BuyerId = Buyer, SellerId = Seller,
            BuyOrderId = buyOrder.OrderId, SellOrderId = sellOrder.OrderId,
        };
        var scope = new TradeBatchScope();

        var (err, _) = await w.Settlement.SettleTradesNoTxAsync(
            new[] { tx }, ordersById, scope, default);

        Assert.Null(err);
        Assert.Equal(-10, w.SellerPos.Quantity);
        Assert.Equal(100m, w.SellerPos.ShortCollateral);
        Assert.Equal(sellerFundReservedBefore + 100m, w.SellerFund.ReservedBalance);

        // Pure short branch fired, not flip.
        Assert.Contains(w.Ledger.PositionEntries, e => e.Action == "ApplyPass:ShortOpen");
        Assert.DoesNotContain(w.Ledger.PositionEntries, e => e.Action == "ApplyPass:Flip:ShortOpen");
        Assert.DoesNotContain(w.Ledger.PositionEntries, e => e.Action == "ApplyPass:Flip:NoopShortPart");
    }

    [Fact]
    public async Task Rollback_restores_short_collateral_and_fund_reserve_to_pre_batch()
    {
        // R4 §0001 + §0008 cross-check: after a real flip with collateral reservation,
        // RestoreCacheSnapshots must roll back BOTH the Fund.ReservedBalance increase AND
        // the Position.ShortCollateral increase to pre-batch values (in lock-step, per the
        // TradeBatchScope.PosShortCollateralSnapshots replay loop at TradeSettler.cs:816-824).
        var w = NewWorld(sellerStartQty: 5, sellerReservedQty: 5);
        var sellOrder = ShortBracketSell(qty: 10, reservedQty: 5, flipQty: 5);
        var buyOrder = BuyerCounterparty(qty: 10, price: 10m);
        var ordersById = new Dictionary<int, Order> { [sellOrder.OrderId] = sellOrder, [buyOrder.OrderId] = buyOrder };

        await w.Accounts.EnsureLoadedAsync(new List<int> { Seller, Buyer }, default);
        RestoreSeededReservations(w);

        var preFundReserved = w.SellerFund.ReservedBalance;
        var preCollateral = w.SellerPos.ShortCollateral;
        var preQuantity = w.SellerPos.Quantity;
        var preOrderSellReserved = sellOrder.CurrentSellReservedQty;

        var tx = new Transaction
        {
            StockId = StockId, CurrencyType = Ccy, Quantity = 10, Price = 10m,
            BuyerId = Buyer, SellerId = Seller,
            BuyOrderId = buyOrder.OrderId, SellOrderId = sellOrder.OrderId,
        };
        var scope = new TradeBatchScope();

        // Per lesson #5: SettleTradesNoTxAsync does not mutate Status; seed the
        // OrderStatusSnapshots dict the way the matcher would have, so the
        // RestoreCacheSnapshots replay loop has something to revert.
        scope.OrderStatusSnapshots.TryAdd(sellOrder.OrderId, sellOrder.Status);
        scope.OrderStatusSnapshots.TryAdd(buyOrder.OrderId, buyOrder.Status);

        var (err, _) = await w.Settlement.SettleTradesNoTxAsync(
            new[] { tx }, ordersById, scope, default);
        Assert.Null(err);
        // Post-settle, mutations landed.
        Assert.Equal(-5, w.SellerPos.Quantity);
        Assert.Equal(50m, w.SellerPos.ShortCollateral);
        Assert.NotEqual(preFundReserved, w.SellerFund.ReservedBalance);

        // Per lesson #5 part 2: simulate the matcher's Status=Filled mutation that would
        // have occurred had the matcher been driven (Order.Fill at Order.cs:404).
        sellOrder.Status = Order.Statuses.Filled;

        // Rollback. The replay loops at TradeSettler.cs:783-858 should restore everything.
        w.Settlement.RestoreCacheSnapshots(ordersById, scope);

        Assert.Equal(preQuantity, w.SellerPos.Quantity);
        Assert.Equal(preCollateral, w.SellerPos.ShortCollateral);
        Assert.Equal(preFundReserved, w.SellerFund.ReservedBalance);
        Assert.Equal(preOrderSellReserved, sellOrder.CurrentSellReservedQty);
        Assert.Equal(Order.Statuses.Open, sellOrder.Status);
    }
}
