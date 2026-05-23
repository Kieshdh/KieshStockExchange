using KieshStockExchange.Models;

namespace KieshStockExchange.Services.MarketEngineServices;

/// <summary>
/// A fill the validate-pass rejected because the seller can't honor it. The caller
/// (OrderExecutionService) cancels the offending maker order, rolls back the matcher's
/// effect on the maker via book.RollbackMakerFill, and reduces the taker's AmountFilled.
/// </summary>
public sealed record RejectedFill(Transaction Trade, int MakerOrderId, string Reason);
