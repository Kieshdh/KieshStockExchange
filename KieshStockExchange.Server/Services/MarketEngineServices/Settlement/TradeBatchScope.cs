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
    public Dictionary<int, (decimal Buy, int Sell)> OrderReservationSnapshots { get; } = new();

    /// <summary> New Positions created mid-batch. Shared across settle calls in one root tx;
    /// caller registers in cache after commit. </summary>
    public Dictionary<(int UserId, int StockId), Position> PendingNewPositions { get; } = new();
}
