using KieshStockExchange.Helpers;

namespace KieshStockExchange.Services.MarketEngineServices.Interfaces;

public interface IOrderEntryService
{
    Task<OrderResult> CancelOrderAsync(int userId, int orderId, CancellationToken ct = default);
    Task<OrderResult> ModifyOrderAsync(int userId, int orderId, int? newQuantity = null,
        decimal? newPrice = null, CancellationToken ct = default);

    // §3.6 P3: modify an armed stop's trigger / stop-limit price / quantity (off-book).
    Task<OrderResult> ModifyStopAsync(int userId, int orderId, int? newQuantity = null,
        decimal? newStopPrice = null, decimal? newLimitPrice = null, CancellationToken ct = default);

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
}
