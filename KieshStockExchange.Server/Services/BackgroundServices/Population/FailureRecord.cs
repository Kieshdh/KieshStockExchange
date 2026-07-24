using KieshStockExchange.Models;
using KieshStockExchange.Services.MarketEngineServices;

namespace KieshStockExchange.Services.BackgroundServices.Helpers;

/// <summary>
/// Broad categories the bot dashboard groups failures into. The raw
/// <see cref="OrderStatus"/> is also kept on each <see cref="FailureRecord"/>,
/// but humans want to know whether the wave is "insufficient shares" vs
/// "insufficient funds" vs "engine error" — not the exact enum branch.
/// </summary>
public enum FailureCategory
{
    InsufficientShares,
    InsufficientFunds,
    NoLiquidity,
    PriceOutOfBounds,
    InvalidOrder,
    AlreadyClosed,
    EngineError,
    Other,
}

public static class FailureCategoryExtensions
{
    /// <summary>Map an order-result status to one of the dashboard buckets.</summary>
    public static FailureCategory ToCategory(this OrderStatus status) => status switch
    {
        OrderStatus.InsufficientStocks  => FailureCategory.InsufficientShares,
        OrderStatus.InsufficientFunds   => FailureCategory.InsufficientFunds,
        OrderStatus.NoLiquidity         => FailureCategory.NoLiquidity,
        OrderStatus.NoMarketPrice       => FailureCategory.NoLiquidity,
        OrderStatus.PriceTooLow         => FailureCategory.PriceOutOfBounds,
        OrderStatus.PriceTooHigh        => FailureCategory.PriceOutOfBounds,
        OrderStatus.InvalidParameters   => FailureCategory.InvalidOrder,
        OrderStatus.NotAuthenticated    => FailureCategory.InvalidOrder,
        OrderStatus.NotAuthorized       => FailureCategory.InvalidOrder,
        OrderStatus.AlreadyClosed       => FailureCategory.AlreadyClosed,
        OrderStatus.OperationFailed     => FailureCategory.EngineError,
        _                               => FailureCategory.Other,
    };

    /// <summary>UI-friendly label for the bucket; what the dashboard renders.</summary>
    public static string DisplayName(this FailureCategory cat) => cat switch
    {
        FailureCategory.InsufficientShares => "Insufficient shares",
        FailureCategory.InsufficientFunds  => "Insufficient funds",
        FailureCategory.NoLiquidity        => "No liquidity",
        FailureCategory.PriceOutOfBounds   => "Price out of bounds",
        FailureCategory.InvalidOrder       => "Invalid order",
        FailureCategory.AlreadyClosed      => "Already closed",
        FailureCategory.EngineError        => "Engine error",
        _                                  => "Other",
    };
}

/// <summary>
/// One bot order that failed to place or match. Stored in a bounded ring inside
/// <see cref="AiTradeService"/> so the dashboard can show breakdowns and the user
/// can export the raw rows to CSV when investigating failure spikes.
/// </summary>
public sealed record FailureRecord(
    DateTime TimestampUtc,
    int AiUserId,
    int UserId,
    int StockId,
    string Side,
    string OrderType,
    int Quantity,
    decimal Price,
    OrderStatus Status,
    FailureCategory Category,
    string ErrorMessage
);
