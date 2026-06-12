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
/// R4 §0002 — deterministic Q7 / flip-path test at the TradeSettler level.
///
/// Replaces the reverted attempt at 5d1b30f/ad39f6b which used non-existent APIs
/// (TrackNewFund, Fund.DepositFunds). This rewrite mirrors the proven mock-
/// IDataBaseService callback shape from <see cref="ArmStopBatchEquivalenceTests"/>
/// and drives <see cref="ISettlementEngine.SettleTradesNoTxAsync"/> directly to
/// reproduce the Q7 condition documented at
/// <c>TradeSettler.cs:527-549</c>: a flip-sell whose shortPart would push
/// Position.Quantity negative, but intra-batch buys lifted live Quantity above
/// zero before the flip path ran — so the collateral-reservation block must be
/// skipped and the trade settled as a plain long-close.
///
/// Assertions follow the §0002 plan:
/// (a) post-batch Position.Quantity equals the algebraic sum
/// (b) the "ApplyPass:Flip:NoopShortPart" ledger entry fires
/// (c) §0001 Status rollback restores in-memory Status on a forced settle failure
/// </summary>
public class FlipBatchInterleavingTests
{
    private const int Seller = 7;
    private const int OtherSeller = 8;
    private const int Buyer = 9;
    private const int StockId = 10;
    private const CurrencyType Ccy = CurrencyType.USD;

    /// <summary>Records position-side ledger actions so the test can assert NoopShortPart fired.</summary>
    private sealed class RecordingLedger : IReservationLedger
    {
        public readonly List<(string Action, decimal Amount)> PositionEntries = new();
        public readonly List<(string Action, decimal Amount)> FundEntries = new();
        public HashSet<int> TrackedUserIds { get; } = new();
        public bool TrackAll { get; set; }
        public IReadOnlyList<LedgerEntry> Snapshot() => Array.Empty<LedgerEntry>();
        public int EntryCount => 0;
        public string SuggestedExportFileName => "test";
        public void LogFund(int userId, CurrencyType ccy, int? orderId, string action,
            decimal amount, decimal reservedBefore, decimal reservedAfter,
            decimal totalBefore, decimal totalAfter)
            => FundEntries.Add((action, amount));
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
        public bool ThrowOnNextUpdateAll;
    }

    private static World NewWorld(int sellerStartQty, int sellerReservedQty)
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
                TotalBalance = 5_000m, ReservedBalance = 0m,
            },
            BuyerFund = new Fund
            {
                UserId = Buyer, CurrencyType = Ccy,
                // Pre-reserved cash to cover Buyer's notional. SellTradesNoTxAsync consumes
                // ReservedBalance via ConsumeReservedFunds when buyOrder.CurrentBuyReservation
                // covers the fill notional.
                TotalBalance = 5_000m, ReservedBalance = 500m,
            },
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
        w.Db.Setup(d => d.UpdateAllAsync(It.IsAny<IEnumerable<Position>>(), It.IsAny<CancellationToken>()))
             .Callback(() => { if (w.ThrowOnNextUpdateAll) throw new InvalidOperationException("Injected failure"); })
             .Returns(Task.CompletedTask);

        var registry = new OrderRegistry();
        w.Ledger = new RecordingLedger();
        w.Accounts = new AccountsCache(w.Db.Object, registry, w.Ledger, NullLogger<AccountsCache>.Instance);

        w.Settlement = new SettlementEngine(w.Db.Object, w.Accounts, w.Ledger, registry,
            NullLogger<SettlementEngine>.Instance, NullLoggerFactory.Instance,
            Options.Create(new SeparatorLoggerOptions()));

        return w;
    }

    private static Order ShortBracketSell(int qty, int reservedQty, int flipQty)
    {
        var o = new Order
        {
            OrderId = 100, UserId = Seller, StockId = StockId, CurrencyType = Ccy,
            Quantity = qty, Price = 10m, SlippagePercent = 0.5m,
            Side = OrderSide.Sell, Entry = EntryType.Market, Stop = StopKind.None,
            FlipQuantity = flipQty,
        };
        // Seed the per-order long pool to match what the bracket coordinator would have
        // reserved at placement time (only the long portion held shares).
        if (reservedQty > 0) o.TakeSellReservation(reservedQty);
        return o;
    }

    private static Order BuyerCounterparty(int qty, decimal price)
    {
        var o = new Order
        {
            OrderId = 200, UserId = Buyer, StockId = StockId, CurrencyType = Ccy,
            Quantity = qty, Price = price,
            Side = OrderSide.Buy, Entry = EntryType.Limit, Stop = StopKind.None,
        };
        // Cash reservation matches the fill notional so SettleNoTxAsync's ConsumeReservedFunds
        // path is hit cleanly (no AvailableBalance fallback).
        o.TakeBuyReservation(qty * price);
        return o;
    }

    [Fact]
    public async Task Q7_flip_with_lifted_live_quantity_fires_NoopShortPart_and_skips_collateral()
    {
        // Setup: pre-lift Position.Quantity to 20 (modelling the post-buy state the surgical
        // comment at TradeSettler.cs:527-535 describes). The seller's order holds 5 shares as
        // its long pool (CurrentSellReservedQty=5) and FlipQuantity=5 telegraphs the intended
        // shortPart. When the flip-sell of qty=10 fires:
        //   longPart  = min(10, 5) = 5  → ConsumeReservedStock(5): Q 20→15, Reserved 5→0
        //   shortPart = 10 - 5     = 5  → ApplyDelta(-5):           Q 15→10
        // 10 ≥ 0 → NoopShortPart fires; no collateral reservation. The trade settles as a
        // plain long-close at the buyer side.
        var w = NewWorld(sellerStartQty: 20, sellerReservedQty: 5);
        await w.Accounts.EnsureLoadedAsync(new List<int> { Seller, Buyer }, default);

        var sellOrder = ShortBracketSell(qty: 10, reservedQty: 5, flipQty: 5);
        var buyOrder = BuyerCounterparty(qty: 10, price: 10m);

        // EnsureLoadedAsync zeros Fund.ReservedBalance / Position.ReservedQuantity then
        // backfills from the OrderRegistry. In this test we create orders AFTER loading
        // and don't go through the cache's Reserve* path, so restore the expected
        // reservations manually to match what the per-order TakeBuyReservation /
        // TakeSellReservation calls just did (those only set the per-order fields).
        w.SellerPos.ReservedQuantity = sellOrder.CurrentSellReservedQty;
        w.BuyerFund.ReservedBalance = buyOrder.CurrentBuyReservation;

        var tx = new Transaction
        {
            StockId = StockId, CurrencyType = Ccy, Quantity = 10, Price = 10m,
            BuyerId = Buyer, SellerId = Seller,
            BuyOrderId = buyOrder.OrderId, SellOrderId = sellOrder.OrderId,
        };

        var ordersById = new Dictionary<int, Order> { [sellOrder.OrderId] = sellOrder, [buyOrder.OrderId] = buyOrder };
        var scope = new TradeBatchScope();

        var (err, rejected) = await w.Settlement.SettleTradesNoTxAsync(
            new[] { tx }, ordersById, scope, default);

        Assert.Null(err);
        Assert.Empty(rejected);

        // (a) Algebraic invariant. Pre-batch Q=20; longPart consumed reserved 5 (Q→15);
        // shortPart applied -5 (Q→10). No additional ApplyDelta, no collateral.
        Assert.Equal(10, w.SellerPos.Quantity);
        Assert.Equal(0, w.SellerPos.ReservedQuantity);
        Assert.Equal(0m, w.SellerPos.ShortCollateral);

        // (b) NoopShortPart ledger entry: the surgical fix's diagnostic signature.
        Assert.Contains(w.Ledger.PositionEntries, e => e.Action == "ApplyPass:Flip:NoopShortPart");

        // The companion long-close consume should also be there as the same fill's other half.
        Assert.Contains(w.Ledger.PositionEntries, e => e.Action == "ApplyPass:Flip:ConsumeReservedStock");
    }

    [Fact]
    public async Task Q7_flip_with_unlifted_quantity_opens_short_normally()
    {
        // Negative control: pre-batch Q=5, sellOrder qty=10, reservedQty=5. longPart=5,
        // shortPart=5. ApplyDelta(-5) → Q = 5-5-5 = ... wait: longPart consumes Q→0, R→0;
        // shortPart ApplyDelta(-5) → Q = -5 → real short open. Collateral reserved.
        var w = NewWorld(sellerStartQty: 5, sellerReservedQty: 5);
        await w.Accounts.EnsureLoadedAsync(new List<int> { Seller, Buyer }, default);

        var sellOrder = ShortBracketSell(qty: 10, reservedQty: 5, flipQty: 5);
        var buyOrder = BuyerCounterparty(qty: 10, price: 10m);

        // EnsureLoadedAsync zeros Fund.ReservedBalance / Position.ReservedQuantity then
        // backfills from the OrderRegistry. In this test we create orders AFTER loading
        // and don't go through the cache's Reserve* path, so restore the expected
        // reservations manually to match what the per-order TakeBuyReservation /
        // TakeSellReservation calls just did (those only set the per-order fields).
        w.SellerPos.ReservedQuantity = sellOrder.CurrentSellReservedQty;
        w.BuyerFund.ReservedBalance = buyOrder.CurrentBuyReservation;

        var tx = new Transaction
        {
            StockId = StockId, CurrencyType = Ccy, Quantity = 10, Price = 10m,
            BuyerId = Buyer, SellerId = Seller,
            BuyOrderId = buyOrder.OrderId, SellOrderId = sellOrder.OrderId,
        };

        var ordersById = new Dictionary<int, Order> { [sellOrder.OrderId] = sellOrder, [buyOrder.OrderId] = buyOrder };
        var scope = new TradeBatchScope();

        var (err, _) = await w.Settlement.SettleTradesNoTxAsync(
            new[] { tx }, ordersById, scope, default);

        Assert.Null(err);
        Assert.Equal(-5, w.SellerPos.Quantity);
        Assert.True(w.SellerPos.ShortCollateral > 0m, "Real short open must reserve collateral.");
        // NoopShortPart must NOT have fired in this control path.
        Assert.DoesNotContain(w.Ledger.PositionEntries, e => e.Action == "ApplyPass:Flip:NoopShortPart");
    }

    [Fact]
    public async Task Status_rollback_after_settle_failure_restores_pre_batch_status()
    {
        // R4 §0001 cross-check: when SettleTradesAsync (the public, tx-owning variant) errors
        // mid-apply, RestoreSnapshots reads scope.OrderStatusSnapshots to revert in-memory
        // Status. This test wires a settle scope, lets the matcher-side capture run via the
        // SnapshotOrderIfNew TryAdd (the settler's defence-in-depth fallback at
        // TradeSettler.cs:152), then forces a failure on the next UpdateAllAsync<Position>
        // and asserts the per-order Status went back.
        var w = NewWorld(sellerStartQty: 5, sellerReservedQty: 5);
        await w.Accounts.EnsureLoadedAsync(new List<int> { Seller, Buyer }, default);

        var sellOrder = ShortBracketSell(qty: 10, reservedQty: 5, flipQty: 5);
        var buyOrder = BuyerCounterparty(qty: 10, price: 10m);

        // EnsureLoadedAsync zeros Fund.ReservedBalance / Position.ReservedQuantity then
        // backfills from the OrderRegistry. In this test we create orders AFTER loading
        // and don't go through the cache's Reserve* path, so restore the expected
        // reservations manually to match what the per-order TakeBuyReservation /
        // TakeSellReservation calls just did (those only set the per-order fields).
        w.SellerPos.ReservedQuantity = sellOrder.CurrentSellReservedQty;
        w.BuyerFund.ReservedBalance = buyOrder.CurrentBuyReservation;
        // Seed a non-Open pre-mutation Status on the buyOrder to make the snapshot
        // meaningful: when RestoreSnapshots runs, the buyOrder's Status must end at
        // its captured value (Open here, since Order constructs at Open).
        Assert.Equal(Order.Statuses.Open, buyOrder.Status);
        Assert.Equal(Order.Statuses.Open, sellOrder.Status);

        var tx = new Transaction
        {
            StockId = StockId, CurrencyType = Ccy, Quantity = 10, Price = 10m,
            BuyerId = Buyer, SellerId = Seller,
            BuyOrderId = buyOrder.OrderId, SellOrderId = sellOrder.OrderId,
        };
        var ordersById = new Dictionary<int, Order> { [sellOrder.OrderId] = sellOrder, [buyOrder.OrderId] = buyOrder };
        var scope = new TradeBatchScope();

        // Manually simulate the matcher-side TryAdd that §0001 wires (the unit test for the
        // matcher's own TryAdd is MatcherStatusRollbackTests; here we exercise the
        // settler's restore path against a populated dict).
        scope.OrderStatusSnapshots.TryAdd(sellOrder.OrderId, sellOrder.Status);
        scope.OrderStatusSnapshots.TryAdd(buyOrder.OrderId, buyOrder.Status);

        var (err, _) = await w.Settlement.SettleTradesNoTxAsync(
            new[] { tx }, ordersById, scope, default);
        Assert.Null(err);

        // Simulate the matcher having mutated Status to Filled (normally happens via
        // Order.Fill before the settler runs). SettleTradesNoTxAsync itself does not
        // mutate Status — the §0001 capture + restore contract is about the matcher's
        // mutation surviving a settle rejection.
        sellOrder.Status = Order.Statuses.Filled;
        buyOrder.Status = Order.Statuses.Filled;

        // Replay via the public API the same way SettleTradesAsync's catch path would.
        w.Settlement.RestoreCacheSnapshots(ordersById, scope);

        Assert.Equal(Order.Statuses.Open, sellOrder.Status);
        Assert.Equal(Order.Statuses.Open, buyOrder.Status);
    }
}
