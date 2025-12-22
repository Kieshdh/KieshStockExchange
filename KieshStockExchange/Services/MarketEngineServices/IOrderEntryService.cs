using KieshStockExchange.Helpers;

namespace KieshStockExchange.Services.MarketEngineServices;

public interface IOrderEntryService
{
    #region Order Operations
    Task<OrderResult> CancelOrderAsync(int userId, int orderId, CancellationToken ct = default);
    Task<OrderResult> ModifyOrderAsync(int userId, int orderId, int? newQuantity = null,
        decimal? newPrice = null, CancellationToken ct = default);
    #endregion

    #region Place Orders
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
    #endregion 
}
