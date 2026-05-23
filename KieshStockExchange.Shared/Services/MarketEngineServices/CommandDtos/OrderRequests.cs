using KieshStockExchange.Helpers;

namespace KieshStockExchange.Services.MarketEngineServices.CommandDtos;

// Phase 3 Step 3: HTTP body shapes for the four /api/orders/* endpoints. One
// generic place endpoint dispatches on Type into the six IOrderEntryService
// Place*Async methods.

public sealed record PlaceOrderRequest(
    int UserId,
    int StockId,
    int Quantity,
    string Type,            // OrderType: "LimitBuy" | "LimitSell" | "SlippageMarketBuy" | "SlippageMarketSell" | "TrueMarketBuy" | "TrueMarketSell"
    CurrencyType Currency,
    decimal? Price,         // populated for Limit orders only
    decimal? SlippagePct,   // populated for SlippageMarket* only
    decimal? BuyBudget);    // populated for TrueMarketBuy only

public sealed record ModifyOrderRequest(
    int UserId,
    int? Quantity,
    decimal? Price);

public sealed record CancelBatchRequest(
    IReadOnlyList<int> OrderIds);
