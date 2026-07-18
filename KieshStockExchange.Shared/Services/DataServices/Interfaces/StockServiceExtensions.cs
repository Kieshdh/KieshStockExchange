namespace KieshStockExchange.Services.DataServices.Interfaces;

/// <summary>Convenience helpers over <see cref="IStockService"/> for row-factory display code.</summary>
public static class StockServiceExtensions
{
    /// <summary>Symbol for the stock, or "-" when the id isn't in the catalog.</summary>
    public static string SymbolOrDash(this IStockService stocks, int stockId)
        => stocks.TryGetSymbol(stockId, out string symbol) ? symbol : "-";
}
