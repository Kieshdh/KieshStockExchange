using KieshStockExchange.Helpers;
using KieshStockExchange.Models;
using KieshStockExchange.Services.PortfolioServices.Helpers;

namespace KieshStockExchange.Services.MarketEngineServices;

/// <summary>
/// Bundle of per-batch snapshot dictionaries that flow through <see cref="ISettlementEngine"/>'s
/// no-tx settle + restore methods. Replaces the four parallel dictionaries the caller used
/// to declare and thread through each call. One scope per outer transaction.
/// </summary>
public sealed class TradeBatchScope
{
    /// <summary>Pre-mutation (TotalBalance, ReservedBalance) for every Fund touched.</summary>
    public Dictionary<(int UserId, CurrencyType Ccy), (decimal Total, decimal Reserved)>
        FundSnapshots { get; } = new();

    /// <summary>Pre-mutation (Quantity, ReservedQuantity) for every existing Position touched.</summary>
    public Dictionary<(int UserId, int StockId), (int Quantity, int Reserved)>
        PosSnapshots { get; } = new();

    /// <summary>Pre-mutation BuyBudget for every TrueMarketBuy order whose budget was decremented.</summary>
    public Dictionary<int, decimal?> BudgetSnapshots { get; } = new();

    /// <summary>Brand-new Positions created during this batch (PositionId == 0). Shared across
    /// multiple settle calls in one root tx so a (user, stock) created in one call is reused
    /// by the next instead of duplicated. Caller registers them in the accounts cache after
    /// the outer tx commits.</summary>
    public Dictionary<(int UserId, int StockId), Position> PendingNewPositions { get; } = new();
}
