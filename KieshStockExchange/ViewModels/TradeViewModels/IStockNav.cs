using KieshStockExchange.Helpers;

namespace KieshStockExchange.ViewModels.TradeViewModels;

// A table row that can navigate the trade page to its stock — backs the ↗ glyph next to the
// symbol in every trade table. GoToStock on TradeTableViewModelBase consumes it.
public interface IStockNav
{
    int StockId { get; }
    CurrencyType Currency { get; }
}
