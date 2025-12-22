using KieshStockExchange.Models;

namespace KieshStockExchange.Services.MarketEngineServices;

public interface IOrderExecutionService
{
    Task<OrderResult> PlaceAndMatchAsync(Order incoming, CancellationToken ct = default);
    Task<OrderResult> CancelOrderAsync(int orderId, CancellationToken ct = default);
    Task<OrderResult> ModifyOrderAsync(int orderId, int? newQuantity = null,
        decimal? newPrice = null, CancellationToken ct = default);
}