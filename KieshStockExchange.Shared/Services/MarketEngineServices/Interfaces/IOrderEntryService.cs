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
    // Round 2 §0007: flipQuantity (optional, default 0) — for Path-2 bracket entries that flip
    // the position. Persisted on the parent so the coordinator can size the SL pool to flipQty.
    Task<OrderResult> PlaceBracketAsync(int userId, int stockId, int quantity, EntryType entry,
        CurrencyType currency, decimal? limitPrice, decimal? buyBudget, decimal? stopPrice,
        decimal? stopLimitPrice, decimal? stopSlippagePct,
        IReadOnlyList<(decimal Price, int Quantity)> takeProfits, CancellationToken ct = default,
        OrderSide side = OrderSide.Buy, int flipQuantity = 0);

    // §3.6 P2 stop orders — armed off-book, promoted when the price crosses stopPrice.
    Task<OrderResult> PlaceStopMarketBuyOrderAsync(int userId, int stockId, int quantity,
        decimal stopPrice, decimal buyBudget, CurrencyType currency, CancellationToken ct = default);

    // Taker-symmetry: a SLIPPAGE-CAPPED market buy-stop (the buy mirror of the capped sell-stop below).
    // Used by the bots' short-protective stops so a buy-stop cascade is bounded per fire (no upward
    // runaway). slippagePct null = fires as a true market buy. Reserves cash at the arm-time anchor.
    Task<OrderResult> PlaceStopMarketBuyOrderAsync(int userId, int stockId, int quantity,
        decimal stopPrice, CurrencyType currency, decimal? slippagePct = null, CancellationToken ct = default);

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

    // §A1a: batch-arm protective sell-stops / trailing-sells (the bot fleet's arm route) in one
    // engine pass — share pre-reserve + ONE bulk-insert tx. One result per request, aligned by
    // index; every success is registered with the trigger watcher before this returns.
    Task<IReadOnlyList<OrderResult>> ArmStopSellBatchAsync(
        IReadOnlyList<StopArmRequest> requests, CancellationToken ct = default);

    /// <summary>Round 2 §0005: batch-place the bot fleet's per-tick bracket cohort. Pre-validates
    /// each request and hands the built parent/SL/TP triples to the engine batch route. One
    /// result per request, aligned by index. Gated by Bots:Advanced:BracketBatch in the caller.</summary>
    Task<IReadOnlyList<OrderResult>> PlaceBracketBatchAsync(
        IReadOnlyList<CommandDtos.BracketBatchRequest> requests, CancellationToken ct = default);

    /// <summary>Round 2 §0005: batch-place the bot fleet's flat-only market short cohort. Same
    /// per-request semantics as PlaceTrueMarketSellOrderAsync. Gated by Bots:Advanced:BracketBatch.</summary>
    Task<IReadOnlyList<OrderResult>> PlaceMarketShortBatchAsync(
        IReadOnlyList<CommandDtos.MarketShortBatchRequest> requests, CancellationToken ct = default);

    /// <summary>STRETCH (Bots:Arbitrage:BatchLegs): batch-place the arb cohort's leg1 true-market
    /// buys in one engine pass. Same per-request semantics as PlaceTrueMarketBuyOrderAsync; one
    /// result per request, aligned by index.</summary>
    Task<IReadOnlyList<OrderResult>> PlaceTrueMarketBuyBatchAsync(
        IReadOnlyList<CommandDtos.TrueMarketBuyBatchRequest> requests, CancellationToken ct = default);

    /// <summary>STRETCH (Bots:Arbitrage:BatchLegs): batch-place the arb cohort's leg2 true-market
    /// sells (sized per bot from each leg1 fill) in one engine pass. Same per-request semantics as
    /// PlaceTrueMarketSellOrderAsync; one result per request, aligned by index.</summary>
    Task<IReadOnlyList<OrderResult>> PlaceTrueMarketSellBatchAsync(
        IReadOnlyList<CommandDtos.TrueMarketSellBatchRequest> requests, CancellationToken ct = default);
}
