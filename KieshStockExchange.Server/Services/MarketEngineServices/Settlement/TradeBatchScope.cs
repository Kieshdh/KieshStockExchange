using KieshStockExchange.Helpers;
using KieshStockExchange.Models;
using KieshStockExchange.Services.PortfolioServices.Helpers;

namespace KieshStockExchange.Services.MarketEngineServices;

/// <summary> Per-batch snapshot bundle. One scope per outer transaction. </summary>
public sealed class TradeBatchScope
{
    /// <summary> Pre-mutation Fund (TotalBalance, ReservedBalance). </summary>
    public Dictionary<(int UserId, CurrencyType Ccy), (decimal Total, decimal Reserved)>
        FundSnapshots { get; } = new();

    /// <summary> Pre-mutation Position (Quantity, ReservedQuantity) for existing rows. </summary>
    public Dictionary<(int UserId, int StockId), (int Quantity, int Reserved)>
        PosSnapshots { get; } = new();

    /// <summary> Pre-mutation BuyBudget for decremented TrueMarketBuy orders. </summary>
    public Dictionary<int, decimal?> BudgetSnapshots { get; } = new();

    /// <summary>
    /// Pre-mutation per-order reservation snapshot
    /// (<see cref="Order.CurrentBuyReservation"/>, <see cref="Order.CurrentSellReservedQty"/>),
    /// captured on first touch in the apply-pass and replayed on rollback so the per-order
    /// field stays in lock-step with the aggregate snapshots above.
    /// </summary>
    public Dictionary<int, (decimal Buy, int Sell, decimal ShortCollateral)> OrderReservationSnapshots { get; } = new();

    /// <summary>
    /// Pre-mutation short-collateral snapshot (<see cref="Position.ShortCollateral"/> +
    /// its currency) for existing positions a short open/close touches. Captured on first
    /// sight and replayed on rollback so collateral stays in lock-step with the Fund/Position
    /// snapshots above. New (PositionId == 0) positions are dropped on rollback, so they
    /// need no entry here.
    /// </summary>
    public Dictionary<(int UserId, int StockId), (decimal Collateral, CurrencyType Ccy)>
        PosShortCollateralSnapshots { get; } = new();

    /// <summary> New Positions created mid-batch. Shared across settle calls in one root tx;
    /// caller registers in cache after commit. </summary>
    public Dictionary<(int UserId, int StockId), Position> PendingNewPositions { get; } = new();

    /// <summary>
    /// R3 §0001 (Q7 follow-up) / R4 §0001 (matcher-side completion): pre-mutation
    /// <see cref="Order.Status"/> snapshot for orders touched by the matcher / settler.
    /// Captured on first touch and replayed on rollback so the in-memory order Status stays
    /// in lock-step with the DB after a settle rejection — closing the same order↔position
    /// desync mode the §P6 precedent at <c>TradeSettler:354-360</c> warned about.
    ///
    /// <para>**Populated by** (R4 §0001, Option A): the matcher captures pre-match Status
    /// immediately before <see cref="Order.Fill(int)"/> mutates it. The two
    /// <c>Order.Fill</c> entry points are:</para>
    /// <list type="bullet">
    ///   <item><c>MatchingEngine.Match</c> for the taker (taker.Fill at MatchingEngine.cs:88).</item>
    ///   <item><c>OrderBook.ApplyMakerFill</c> for each maker (maker.Fill at OrderBook.cs:171).</item>
    /// </list>
    /// <para>Both methods take an optional <c>TradeBatchScope? scope</c> param and do a
    /// <c>scope?.OrderStatusSnapshots.TryAdd(orderId, status)</c> guard. The
    /// <c>OrderExecutionService.cs:969</c> batch group dispatch threads <c>groupScope</c>;
    /// single-taker call sites (<c>:155</c>, <c>:469</c>) pass null and rely on
    /// <c>RollbackMatch</c>'s Status=Open hardcode (correct because book makers are always
    /// Open by construction — <see cref="OrderBook.BulkLoad"/> filters on cold-load).</para>
    /// <para>The defence-in-depth first-sight TryAdd at <see cref="TradeSettler"/>
    /// <c>SnapshotOrderIfNew</c> remains idempotent: matcher-side capture wins because it
    /// runs first.</para>
    /// </summary>
    public Dictionary<int, string> OrderStatusSnapshots { get; } = new();
}
