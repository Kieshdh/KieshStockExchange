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
/// Share-conservation invariant at the settlement level: a matched buy+sell MOVES shares from the
/// seller to the buyer but never CREATES or DESTROYS them — Σ(Position.Quantity) for a stock is the
/// same before and after a settled batch. This is the share half of <see cref="ConservationProbe"/>'s
/// contract, exercised end-to-end through <see cref="ISettlementEngine.SettleTradesNoTxAsync"/> (the
/// same path the live engine runs). Harness mirrors FlipBatchInterleavingTests.
/// </summary>
public class ShareConservationTests
{
    private const int Seller = 7;
    private const int Buyer = 9;
    private const int StockId = 10;
    private const CurrencyType Ccy = CurrencyType.USD;

    private sealed class NoopLedger : IReservationLedger
    {
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
            int quantityBefore, int quantityAfter) { }
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
        public Position SellerPos = null!;
        public Position BuyerPos = null!;
        public Fund SellerFund = null!;
        public Fund BuyerFund = null!;
    }

    private static World NewWorld(int sellerStartQty, int buyerStartQty, int sellerReservedQty)
    {
        var w = new World
        {
            SellerPos = new Position { PositionId = 1, UserId = Seller, StockId = StockId, Quantity = sellerStartQty, ReservedQuantity = sellerReservedQty },
            BuyerPos  = new Position { PositionId = 2, UserId = Buyer,  StockId = StockId, Quantity = buyerStartQty,  ReservedQuantity = 0 },
            SellerFund = new Fund { UserId = Seller, CurrencyType = Ccy, TotalBalance = 100_000m, ReservedBalance = 0m },
            BuyerFund  = new Fund { UserId = Buyer,  CurrencyType = Ccy, TotalBalance = 100_000m, ReservedBalance = 0m },
        };

        var db = new Mock<IDataBaseService>(MockBehavior.Loose);
        db.Setup(d => d.GetFundsForUsersAsync(It.IsAny<List<int>>(), It.IsAny<CancellationToken>()))
          .ReturnsAsync(new List<Fund> { w.SellerFund, w.BuyerFund });
        db.Setup(d => d.GetPositionsForUsersAsync(It.IsAny<List<int>>(), It.IsAny<CancellationToken>()))
          .ReturnsAsync(new List<Position> { w.SellerPos, w.BuyerPos });
        db.Setup(d => d.GetOpenOrdersForUsersAsync(It.IsAny<List<int>>(), It.IsAny<CancellationToken>()))
          .ReturnsAsync(new List<Order>());
        db.Setup(d => d.BeginTransactionAsync(It.IsAny<CancellationToken>()))
          .ReturnsAsync(new Mock<ITransaction>().Object);
        db.Setup(d => d.UpdateAllAsync(It.IsAny<IEnumerable<Position>>(), It.IsAny<CancellationToken>()))
          .Returns(Task.CompletedTask);

        var registry = new OrderRegistry();
        var ledger = new NoopLedger();
        w.Accounts = new AccountsCache(db.Object, registry, ledger, NullLogger<AccountsCache>.Instance);
        w.Settlement = new SettlementEngine(db.Object, w.Accounts, ledger, registry,
            NullLogger<SettlementEngine>.Instance, NullLoggerFactory.Instance,
            Options.Create(new SeparatorLoggerOptions()));
        return w;
    }

    private static Order Sell(int qty, int reservedQty, decimal price)
    {
        var o = new Order
        {
            OrderId = 100, UserId = Seller, StockId = StockId, CurrencyType = Ccy,
            Quantity = qty, Price = price, Side = OrderSide.Sell, Entry = EntryType.Limit, Stop = StopKind.None,
        };
        if (reservedQty > 0) o.TakeSellReservation(reservedQty);
        return o;
    }

    private static Order Buy(int qty, decimal price)
    {
        var o = new Order
        {
            OrderId = 200, UserId = Buyer, StockId = StockId, CurrencyType = Ccy,
            Quantity = qty, Price = price, Side = OrderSide.Buy, Entry = EntryType.Limit, Stop = StopKind.None,
        };
        o.TakeBuyReservation(qty * price);
        return o;
    }

    [Fact]
    public async Task Settled_trade_moves_shares_seller_to_buyer_without_creating_or_destroying()
    {
        const int qty = 10; const decimal price = 10m;
        var w = NewWorld(sellerStartQty: 100, buyerStartQty: 0, sellerReservedQty: qty);
        await w.Accounts.EnsureLoadedAsync(new List<int> { Seller, Buyer }, default);

        var sell = Sell(qty, reservedQty: qty, price);
        var buy = Buy(qty, price);
        // Orders created after EnsureLoadedAsync (which zeros reservations) — restore the per-order
        // reservations onto the cached account state, mirroring FlipBatchInterleavingTests.
        w.SellerPos.ReservedQuantity = sell.CurrentSellReservedQty;
        w.BuyerFund.ReservedBalance = buy.CurrentBuyReservation;

        int sharesBefore = w.SellerPos.Quantity + w.BuyerPos.Quantity;

        var tx = new Transaction
        {
            StockId = StockId, CurrencyType = Ccy, Quantity = qty, Price = price,
            BuyerId = Buyer, SellerId = Seller, BuyOrderId = buy.OrderId, SellOrderId = sell.OrderId,
        };
        var ordersById = new Dictionary<int, Order> { [sell.OrderId] = sell, [buy.OrderId] = buy };

        var (err, rejected) = await w.Settlement.SettleTradesNoTxAsync(
            new[] { tx }, ordersById, new TradeBatchScope(), default);

        Assert.Null(err);
        Assert.Empty(rejected);

        // Shares moved seller→buyer; total per stock unchanged (none created or destroyed).
        Assert.Equal(90, w.SellerPos.Quantity);
        Assert.Equal(10, w.BuyerPos.Quantity);
        Assert.Equal(sharesBefore, w.SellerPos.Quantity + w.BuyerPos.Quantity);
    }

    [Fact]
    public async Task Multi_fill_batch_conserves_total_shares_per_stock()
    {
        // Two fills against the same seller in one batch: 6 then 4 shares to the buyer.
        const decimal price = 10m;
        var w = NewWorld(sellerStartQty: 100, buyerStartQty: 5, sellerReservedQty: 10);
        await w.Accounts.EnsureLoadedAsync(new List<int> { Seller, Buyer }, default);

        var sell = Sell(qty: 10, reservedQty: 10, price);
        var buy = Buy(qty: 10, price);
        w.SellerPos.ReservedQuantity = sell.CurrentSellReservedQty;
        w.BuyerFund.ReservedBalance = buy.CurrentBuyReservation;

        int sharesBefore = w.SellerPos.Quantity + w.BuyerPos.Quantity;

        var ordersById = new Dictionary<int, Order> { [sell.OrderId] = sell, [buy.OrderId] = buy };
        var txs = new[]
        {
            new Transaction { StockId = StockId, CurrencyType = Ccy, Quantity = 6, Price = price, BuyerId = Buyer, SellerId = Seller, BuyOrderId = buy.OrderId, SellOrderId = sell.OrderId },
            new Transaction { StockId = StockId, CurrencyType = Ccy, Quantity = 4, Price = price, BuyerId = Buyer, SellerId = Seller, BuyOrderId = buy.OrderId, SellOrderId = sell.OrderId },
        };

        var (err, _) = await w.Settlement.SettleTradesNoTxAsync(txs, ordersById, new TradeBatchScope(), default);

        Assert.Null(err);
        Assert.Equal(sharesBefore, w.SellerPos.Quantity + w.BuyerPos.Quantity);
        Assert.Equal(90, w.SellerPos.Quantity);
        Assert.Equal(15, w.BuyerPos.Quantity);
    }
}
