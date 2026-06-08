using KieshStockExchange.Helpers;
using KieshStockExchange.Models;

namespace KieshStockExchange.Services.MarketEngineServices.Interfaces;

public interface IOrderEntryService
{
    Task<OrderResult> CancelOrderAsync(int userId, int orderId, CancellationToken ct = default);
    Task<OrderResult> ModifyOrderAsync(int userId, int orderId, int? newQuantity = null,
        decimal? newPrice = null, CancellationToken ct = default);

    // §3.6 P3: modify an armed stop's trigger / stop-limit price / quantity (off-book).
    Task<OrderResult> ModifyStopAsync(int userId, int orderId, int? newQuantity = null,
        decimal? newStopPrice = null, decimal? newLimitPrice = null, CancellationToken ct = default);

    // §F5: modify a bracket leg (the SL or a TP), dormant or live. Dispatches by leg status —
    // a dormant (Attached) leg is edited in place + re-validated against the bracket geometry; a
    // live leg delegates to ModifyStopAsync (armed SL) / ModifyOrderAsync (resting TP). newPrice is
    // the leg's stop price (SL) or limit price (TP).
    Task<OrderResult> ModifyBracketLegAsync(int userId, int legId, decimal newPrice, int newQuantity,
        CancellationToken ct = default);

    Task<OrderResult> PlaceLimitBuyOrderAsync(int userId, int stockId, int quantity,
        decimal limitPrice, CurrencyType currency, CancellationToken ct = default);

    Task<OrderResult> PlaceLimitSellOrderAsync(int userId, int stockId, int quantity,
        decimal limitPrice, CurrencyType currency, CancellationToken ct = default);

    Task<OrderResult> PlaceSlippageMarketBuyOrderAsync(int userId, int stockId, int quantity,
        decimal slippagePct, CurrencyType currency, CancellationToken ct = default);

    Task<OrderResult> PlaceSlippageMarketSellOrderAsync(int userId, int stockId, int quantity,
        decimal slippagePct, CurrencyType currency, CancellationToken ct = default);

    Task<OrderResult> PlaceTrueMarketBuyOrderAsync(int userId, int stockId, int quantity,
        decimal buyBudget, CurrencyType currency, CancellationToken ct = default);

    Task<OrderResult> PlaceTrueMarketSellOrderAsync(int userId, int stockId,
        int quantity, CurrencyType currency, CancellationToken ct = default);

    // §3.6 P4: place a (long) bracket — a buy entry + an optional protective stop-loss and/or up to
    // three scale-out take-profit limits. entry = Limit (limitPrice) or Market (buyBudget). stopPrice
    // null ⇒ a take-profit-only bracket (no protective stop); then ≥1 take-profit is required.
    // stopLimitPrice set ⇒ the SL is a stop-limit; null ⇒ a stop-market (stopSlippagePct optionally
    // caps it). Each take-profit is (price, quantity); Σ TP qty ≤ quantity, TP prices strictly
    // ascending above entry, SL below.
    Task<OrderResult> PlaceBracketAsync(int userId, int stockId, int quantity, EntryType entry,
        CurrencyType currency, decimal? limitPrice, decimal? buyBudget, decimal? stopPrice,
        decimal? stopLimitPrice, decimal? stopSlippagePct,
        IReadOnlyList<(decimal Price, int Quantity)> takeProfits, CancellationToken ct = default,
        OrderSide side = OrderSide.Buy);

    // §3.6 P2 stop orders — armed off-book, promoted when the price crosses stopPrice.
    Task<OrderResult> PlaceStopMarketBuyOrderAsync(int userId, int stockId, int quantity,
        decimal stopPrice, decimal buyBudget, CurrencyType currency, CancellationToken ct = default);

    // §3.6: a sell-stop may fire as a slippage-capped market order (a guard so a stop-loss
    // doesn't dump at any price). slippagePct null = fires as a true market sell.
    Task<OrderResult> PlaceStopMarketSellOrderAsync(int userId, int stockId, int quantity,
        decimal stopPrice, CurrencyType currency, decimal? slippagePct = null, CancellationToken ct = default);

    Task<OrderResult> PlaceStopLimitBuyOrderAsync(int userId, int stockId, int quantity,
        decimal stopPrice, decimal limitPrice, CurrencyType currency, CancellationToken ct = default);

    Task<OrderResult> PlaceStopLimitSellOrderAsync(int userId, int stockId, int quantity,
        decimal stopPrice, decimal limitPrice, CurrencyType currency, CancellationToken ct = default);

    // §3.6 P5 trailing stops (market-only). The trigger trails a monotonic watermark by trailOffset
    // (absolute, or a 0–100 percent of the watermark when isPercent). A buy-trail funds its market
    // fill from buyBudget; a sell-trail reserves shares like a static sell-stop.
    Task<OrderResult> PlaceTrailingStopBuyOrderAsync(int userId, int stockId, int quantity,
        decimal trailOffset, bool isPercent, decimal buyBudget, CurrencyType currency, CancellationToken ct = default);

    Task<OrderResult> PlaceTrailingStopSellOrderAsync(int userId, int stockId, int quantity,
        decimal trailOffset, bool isPercent, CurrencyType currency, CancellationToken ct = default);
}
