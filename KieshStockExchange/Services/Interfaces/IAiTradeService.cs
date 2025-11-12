using KieshStockExchange.Helpers;
using KieshStockExchange.Models;

namespace KieshStockExchange.Services;

public interface IAiTradeService
{
    //Task<bool> RefreshStateAsync(AIUser aiUser, CurrencyType currency, CancellationToken ct = default);

    Task<Order?> ComputeOrderAsync(AIUser aiUser, CurrencyType currency, CancellationToken ct = default);

    Task<OrderResult?> ExecuteOrderAsync(AIUser aiUser, Order order, CancellationToken ct = default);
}
