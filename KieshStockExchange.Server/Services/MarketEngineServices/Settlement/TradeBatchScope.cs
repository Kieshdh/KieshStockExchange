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
    /// R3 §0001 (Q7 follow-up): pre-mutation <see cref="Order.Status"/> snapshot for orders
    /// touched by the matcher / settler. Captured on first touch and replayed on rollback so
    /// the in-memory order Status stays in lock-step with the DB after a settle rejection —
    /// closing the same order↔position desync mode the §P6 precedent at
    /// <c>TradeSettler:354-360</c> warned about.
    ///
    /// **WHERE TO POPULATE** (local Claude action): the matcher in
    /// <c>OrderExecutionService.cs</c> mutates Status to <see cref="Order.Statuses.Filled"/> /
    /// <see cref="Order.Statuses.PartiallyFilled"/> in the maker/taker apply paths near
    /// <c>:2010, 2028, 2040</c> and in <c>RestoreOrderToBook</c> at <c>:1949</c>. Add a
    /// <c>scope.OrderStatusSnapshots.TryAdd(o.OrderId, o.Status)</c> guard at the same point
    /// the matcher captures order-state for rollback (the existing reservation-snapshot
    /// pathway). The pre-write rejector in <see cref="TradeSettler"/> reads from this dict in
    /// <c>RestoreSnapshots</c> to roll the in-memory Status back to its pre-batch value.
    /// </summary>
    public Dictionary<int, string> OrderStatusSnapshots { get; } = new();
}
