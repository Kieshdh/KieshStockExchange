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
// set ⇒ the SL is a stop-limit; null ⇒ a stop-market (StopSlippagePct optionally caps it).
// §P5b: Side selects long (Buy entry → sell legs, default) vs short (Sell entry → buy-to-close legs).
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
    IReadOnlyList<BracketLeg> TakeProfits,
    OrderSide Side = OrderSide.Buy);

// Round 2 §0005: batched bracket placement — bot-fleet entry point for SubmitAdvancedAsync's
// bracket cohort. Same payload as PlaceBracketRequest; the batch route consolidates the per-tick
// bracket placements behind Bots:Advanced:BracketBatch so the singles path (per-order
// PlaceBracketAsync) remains a safe rollback target.
public sealed record BracketBatchRequest(
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
    IReadOnlyList<BracketLeg> TakeProfits,
    OrderSide Side = OrderSide.Buy,
    // Round 2 §0007 (Path 2): flip portion of the entry. 0 for round-trip-only and Path-1 minimal.
    int FlipQuantity = 0);

// Round 2 §0005: batched flat-only market short open — the bot fleet's short cohort.
// Mirrors PlaceTrueMarketSellOrderAsync's signature; collected by SubmitAdvancedAsync when
// Bots:Advanced:BracketBatch is on.
public sealed record MarketShortBatchRequest(
    int UserId,
    int StockId,
    int Quantity,
    CurrencyType Currency);
