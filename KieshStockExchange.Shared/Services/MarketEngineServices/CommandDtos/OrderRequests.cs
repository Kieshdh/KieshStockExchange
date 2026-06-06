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
    decimal? StopPrice = null,    // trigger level when Stop != None
    decimal? TrailOffset = null,  // §P5 trailing offset (absolute, or 0–100% when TrailIsPercent)
    bool? TrailIsPercent = null); // §P5 true = TrailOffset is a percent of the watermark

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

// §F5: HTTP body for /api/orders/{id}/modify-leg. Edit one bracket leg (the SL or a TP), dormant or
// live. Price = the leg's stop price (SL) or limit price (TP); Quantity = the leg quantity.
public sealed record ModifyBracketLegRequest(
    int UserId,
    decimal Price,
    int Quantity);

// §3.6 P4: one take-profit leg of a bracket — a limit exit at Price for Quantity shares.
public sealed record BracketLeg(decimal Price, int Quantity);

// §3.6 P4: place a (long) bracket — a buy entry plus an optional protective stop-loss and/or up to
// three scale-out take-profit legs, OCO-grouped, armed as the parent fills. Entry = Market (BuyBudget)
// or Limit (Price). StopPrice null ⇒ take-profit-only bracket (no protective stop). StopLimitPrice
// set ⇒ the SL is a stop-limit; null ⇒ a stop-market (StopSlippagePct optionally caps it). Short
// brackets are not yet supported (rejected server-side).
public sealed record PlaceBracketRequest(
    int UserId,
    int StockId,
    int Quantity,
    EntryType Entry,
    CurrencyType Currency,
    decimal? Price,
    decimal? BuyBudget,
    decimal? StopPrice,
    decimal? StopLimitPrice,
    decimal? StopSlippagePct,
    IReadOnlyList<BracketLeg> TakeProfits);
