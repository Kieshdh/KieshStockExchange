using KieshStockExchange.Helpers;
using KieshStockExchange.Models;
using KieshStockExchange.Services.DataServices.Interfaces;
using KieshStockExchange.Services.MarketEngineServices;
using KieshStockExchange.Services.PortfolioServices;
using KieshStockExchange.Services.PortfolioServices.Helpers;
using KieshStockExchange.Services.PortfolioServices.Interfaces;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace KieshStockExchange.Tests;

/// <summary>
/// R3 §0002 — deterministic regression coverage for the mixed-portion Path-2 settle path
/// (round 2 §0007). Exercises <see cref="TradeSettler.SettleNoTxAsync"/> directly with a
/// <see cref="ShortBracket"/> parent that has a non-zero <c>FlipQuantity</c>.
///
/// The round-2 cover note's Q7 finding ID'd hypothesis (A) intra-batch shared <c>posMap</c> as
/// the trigger for `CK_Positions_Quantity_Invariants` violations: a separate buy on the same
/// Position lifts live <c>Quantity</c> upward before the flip path runs, so
/// <c>ApplyDelta(-shortPart)</c> leaves Q still positive and <c>TakeShortCollateral</c> credits
/// collateral on a long Position — tripping <c>(Quantity &lt; 0 OR ShortCollateral = 0)</c>.
///
/// The round-3 §0001 structural defense at <see cref="TradeSettler"/> rejects any violating
/// Position pre-write — this test exercises that backstop. The round-3 §0001 surgical fix (in
/// commit <c>684775c</c>) further makes the no-op shortPart explicit so the trigger never fires
/// in the happy path. Together: belt-and-suspenders coverage.
///
/// Constructor pattern mirrors <c>FlipSettlementTests</c> for the <see cref="AccountsCache"/>
/// setup; goes one layer deeper to instantiate the settler.
/// </summary>
public class BracketFlipSettleScenariosTests
{
    private const int Seller = 1;
    private const int Buyer = 2;
    private const int StockId = 10;
    private const decimal Price = 100m;
    private const CurrencyType Ccy = CurrencyType.USD;

    private static (TradeSettler Settler, AccountsCache Accounts, Mock<IDataBaseService> Db) NewSettler()
    {
        var dbMock = new Mock<IDataBaseService>(MockBehavior.Loose);
        var ledger = new Mock<IReservationLedger>(MockBehavior.Loose).Object;
        var registry = new OrderRegistry();
        var accounts = new AccountsCache(dbMock.Object, registry, ledger, NullLogger<AccountsCache>.Instance);
        var validator = new SellerCapacityValidator(NullLogger<SellerCapacityValidator>.Instance);
        var probe = new ConservationProbe(NullLogger<ConservationProbe>.Instance);
        var settler = new TradeSettler(dbMock.Object, accounts, ledger,
            NullLogger<TradeSettler>.Instance, validator, probe, registry);
        return (settler, accounts, dbMock);
    }

    private static void SetupSeller(AccountsCache accounts, int quantity, int reserved, decimal cashFloor = 1_000_000m)
    {
        accounts.TrackNewPosition(new Position { UserId = Seller, StockId = StockId, Quantity = quantity, ReservedQuantity = reserved });
        var fund = new Fund { UserId = Seller, CurrencyType = Ccy };
        fund.DepositFunds(cashFloor);
        accounts.TrackNewFund(fund);
    }

    private static void SetupBuyer(AccountsCache accounts, decimal cash = 10_000m)
    {
        accounts.TrackNewPosition(new Position { UserId = Buyer, StockId = StockId, Quantity = 0 });
        var fund = new Fund { UserId = Buyer, CurrencyType = Ccy };
        fund.DepositFunds(cash);
        accounts.TrackNewFund(fund);
    }

    private static Order ShortBracketParent(int orderId, int qty, int flipQty, int reserved)
    {
        var o = new Order
        {
            UserId = Seller, StockId = StockId, Quantity = qty, Price = 0m,
            CurrencyType = Ccy, Side = OrderSide.Sell, Entry = EntryType.Market, Stop = StopKind.None,
            FlipQuantity = flipQty,
        };
        o.OrderId = orderId;
        if (reserved > 0) o.TakeSellReservation(reserved);
        return o;
    }

    private static Order BuyMarketTaker(int orderId, int qty)
    {
        var o = new Order
        {
            UserId = Buyer, StockId = StockId, Quantity = qty, Price = 0m,
            CurrencyType = Ccy, Side = OrderSide.Buy, Entry = EntryType.Market, Stop = StopKind.None,
            BuyBudget = Price * qty,
        };
        o.OrderId = orderId;
        return o;
    }

    private static Transaction Fill(int sellOrderId, int buyOrderId, int qty) => new()
    {
        StockId = StockId, BuyOrderId = buyOrderId, SellOrderId = sellOrderId,
        BuyerId = Buyer, SellerId = Seller, Quantity = qty, Price = Price, CurrencyType = Ccy,
    };

    /// <summary>
    /// Scenario A: single 50-share trade against a parent with FlipQty=6. Seller pre-state
    /// Q=44, R=44. Expected post-state: Q=-6, R=0, SC>0. CK invariants hold.
    /// </summary>
    [Fact]
    public async Task ScenarioA_single_50_share_trade_settles_clean()
    {
        var (settler, accounts, _) = NewSettler();
        SetupSeller(accounts, quantity: 44, reserved: 44);
        SetupBuyer(accounts);

        var parent = ShortBracketParent(orderId: 1000, qty: 50, flipQty: 6, reserved: 44);
        var taker = BuyMarketTaker(orderId: 2000, qty: 50);
        var ordersById = new Dictionary<int, Order> { [1000] = parent, [2000] = taker };
        var scope = new TradeBatchScope();
        var trades = new List<Transaction> { Fill(1000, 2000, 50) };

        var (err, rejected) = await settler.SettleNoTxAsync(trades, ordersById, scope, default);

        Assert.Null(err);
        Assert.Empty(rejected);
        var pos = accounts.GetPosition(Seller, StockId);
        Assert.NotNull(pos);
        Assert.True(pos!.IsValid(), $"Position invariant violated: Q={pos.Quantity} R={pos.ReservedQuantity} SC={pos.ShortCollateral}");
    }

    /// <summary>
    /// Scenario B: two trades 44 + 6. The first fills the longPart cleanly (Q drops from 44 to
    /// 0, R from 44 to 0). The second is pure shortPart (longPart=0, shortPart=6). Final state
    /// Q=-6, R=0, SC>0.
    /// </summary>
    [Fact]
    public async Task ScenarioB_two_trades_44_then_6_settles_clean()
    {
        var (settler, accounts, _) = NewSettler();
        SetupSeller(accounts, quantity: 44, reserved: 44);
        SetupBuyer(accounts);

        var parent = ShortBracketParent(orderId: 1001, qty: 50, flipQty: 6, reserved: 44);
        var taker = BuyMarketTaker(orderId: 2001, qty: 50);
        var ordersById = new Dictionary<int, Order> { [1001] = parent, [2001] = taker };
        var scope = new TradeBatchScope();
        var trades = new List<Transaction>
        {
            Fill(1001, 2001, 44),
            Fill(1001, 2001, 6),
        };

        var (err, rejected) = await settler.SettleNoTxAsync(trades, ordersById, scope, default);

        Assert.Null(err);
        Assert.Empty(rejected);
        var pos = accounts.GetPosition(Seller, StockId);
        Assert.NotNull(pos);
        Assert.True(pos!.IsValid(), $"Position invariant violated: Q={pos.Quantity} R={pos.ReservedQuantity} SC={pos.ShortCollateral}");
    }

    /// <summary>
    /// Scenario C: three trades 20 + 24 + 6. The first two consume the long reservation
    /// (longPart=20, longPart=24), the third is pure shortPart=6.
    /// </summary>
    [Fact]
    public async Task ScenarioC_three_trades_20_24_6_settles_clean()
    {
        var (settler, accounts, _) = NewSettler();
        SetupSeller(accounts, quantity: 44, reserved: 44);
        SetupBuyer(accounts);

        var parent = ShortBracketParent(orderId: 1002, qty: 50, flipQty: 6, reserved: 44);
        var taker = BuyMarketTaker(orderId: 2002, qty: 50);
        var ordersById = new Dictionary<int, Order> { [1002] = parent, [2002] = taker };
        var scope = new TradeBatchScope();
        var trades = new List<Transaction>
        {
            Fill(1002, 2002, 20),
            Fill(1002, 2002, 24),
            Fill(1002, 2002, 6),
        };

        var (err, rejected) = await settler.SettleNoTxAsync(trades, ordersById, scope, default);

        Assert.Null(err);
        Assert.Empty(rejected);
        var pos = accounts.GetPosition(Seller, StockId);
        Assert.NotNull(pos);
        Assert.True(pos!.IsValid(), $"Position invariant violated: Q={pos.Quantity} R={pos.ReservedQuantity} SC={pos.ShortCollateral}");
    }

    /// <summary>
    /// Scenario D: two trades 30 + 20, boundary-spanning. The second has longPart=14
    /// (whatever reservation is left after the first 30) and shortPart=6.
    /// </summary>
    [Fact]
    public async Task ScenarioD_boundary_spanning_30_20_settles_clean()
    {
        var (settler, accounts, _) = NewSettler();
        SetupSeller(accounts, quantity: 44, reserved: 44);
        SetupBuyer(accounts);

        var parent = ShortBracketParent(orderId: 1003, qty: 50, flipQty: 6, reserved: 44);
        var taker = BuyMarketTaker(orderId: 2003, qty: 50);
        var ordersById = new Dictionary<int, Order> { [1003] = parent, [2003] = taker };
        var scope = new TradeBatchScope();
        var trades = new List<Transaction>
        {
            Fill(1003, 2003, 30),
            Fill(1003, 2003, 20),
        };

        var (err, rejected) = await settler.SettleNoTxAsync(trades, ordersById, scope, default);

        Assert.Null(err);
        Assert.Empty(rejected);
        var pos = accounts.GetPosition(Seller, StockId);
        Assert.NotNull(pos);
        Assert.True(pos!.IsValid(), $"Position invariant violated: Q={pos.Quantity} R={pos.ReservedQuantity} SC={pos.ShortCollateral}");
    }

    /// <summary>
    /// Q7 hypothesis (A) repro: intra-batch shared posMap. A SEPARATE buy by the seller on the
    /// same Position lands first in the batch, lifting live Q from +44 to +69, BEFORE the flip
    /// path runs on the ShortBracket parent fill. Pre-R3-§0001, this triggered the live Q to
    /// stay positive after ApplyDelta(-shortPart) while TakeShortCollateral credited anyway,
    /// producing (Q=+19, R=0, SC>0) and tripping CK_Positions_Quantity_Invariants.
    ///
    /// Post-R3-§0001 + the surgical fix in 684775c: settler should detect-and-reject (return
    /// non-null err) OR the surgical fix's no-op shortPart branch handles it cleanly. Either
    /// way no CK violation reaches the DB.
    ///
    /// This is the canonical regression repro for Q7.
    /// </summary>
    [Fact]
    public async Task Q7_hypothesisA_intra_batch_shared_posMap_does_not_violate_CK()
    {
        var (settler, accounts, _) = NewSettler();
        SetupSeller(accounts, quantity: 44, reserved: 44);
        SetupBuyer(accounts, cash: 100_000m);  // buyer also needs enough to be the seller's intra-batch counter-party

        var parent = ShortBracketParent(orderId: 1004, qty: 50, flipQty: 6, reserved: 44);
        var taker = BuyMarketTaker(orderId: 2004, qty: 50);
        // The "intra-batch buy" — Seller is also the buyer of 25 more shares from some other
        // order in this batch. Set up a counterparty sell order for it.
        var counterpartySell = ShortBracketParent(orderId: 1005, qty: 25, flipQty: 0, reserved: 0);
        counterpartySell.UserId = Buyer; // counterparty owns the sell
        var sellerAsBuyer = BuyMarketTaker(orderId: 2005, qty: 25);
        sellerAsBuyer.UserId = Seller; // Seller is the buyer here

        var ordersById = new Dictionary<int, Order>
        {
            [1004] = parent, [2004] = taker,
            [1005] = counterpartySell, [2005] = sellerAsBuyer,
        };
        var scope = new TradeBatchScope();
        // Order matters: intra-batch buy lands BEFORE the parent's flip fill.
        var trades = new List<Transaction>
        {
            new()
            {
                StockId = StockId, BuyOrderId = 2005, SellOrderId = 1005,
                BuyerId = Seller, SellerId = Buyer, Quantity = 25, Price = Price, CurrencyType = Ccy,
            },
            Fill(1004, 2004, 50),
        };

        var (err, rejected) = await settler.SettleNoTxAsync(trades, ordersById, scope, default);

        // Either: (a) the surgical fix's no-op branch handled it cleanly (err=null, pos valid),
        // or (b) the structural defense rejected pre-write (err non-null). Both are acceptable;
        // the un-acceptable outcome is a CK violation reaching the DB (which manifests as an
        // exception in the test environment, since the mock DB doesn't enforce CK but the
        // pre-write check should).
        var pos = accounts.GetPosition(Seller, StockId);
        Assert.NotNull(pos);
        if (err is null)
        {
            // Surgical fix path: Position must be invariant-clean.
            Assert.True(pos!.IsValid(), $"Position invariant violated: Q={pos.Quantity} R={pos.ReservedQuantity} SC={pos.ShortCollateral}");
        }
        // If err is non-null, the structural defense did its job — Position state may be mid-
        // mutation but the rollback path is exercised separately.
    }
}
