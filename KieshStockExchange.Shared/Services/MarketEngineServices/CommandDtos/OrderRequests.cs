using KieshStockExchange.Helpers;
using KieshStockExchange.Models;

namespace KieshStockExchange.Services.MarketEngineServices.CommandDtos;

// Phase 3 Step 3: HTTP body for /api/orders/place. §3.6 decomposition — the type is the three
// orthogonal dimensions (Side/Entry/Stop) plus the optional value fields; the controller maps the
// combination to the matching named IOrderEntryService.Place*Async method.

public sealed record PlaceOrderRequest(
    int UserId,
    int StockId,
    int Quantity,
    OrderSide Side,         // Buy | Sell
    EntryType Entry,        // Limit | Market
    StopKind Stop,          // None | Stop | (Trailing — P3)
    CurrencyType Currency,
    decimal? Price,         // limit price (Limit / StopLimit) or slippage anchor (capped Market)
    decimal? SlippagePct,   // set = slippage cap on a Market entry (incl. a stop firing as capped market)
    decimal? BuyBudget,     // budget for an uncapped Market buy (true market / stop-market→true)
    decimal? StopPrice = null); // trigger level when Stop != None

public sealed record ModifyOrderRequest(
    int UserId,
    int? Quantity,
    decimal? Price);

// §3.6 P3: HTTP body for /api/orders/{id}/modify-stop. Modifies an armed stop's trigger
// (StopPrice), its stop-limit price (LimitPrice, stop-limit only), and/or its quantity.
public sealed record ModifyStopRequest(
    int UserId,
    int? Quantity,
    decimal? StopPrice,
    decimal? LimitPrice);

public sealed record CancelBatchRequest(
    IReadOnlyList<int> OrderIds);
