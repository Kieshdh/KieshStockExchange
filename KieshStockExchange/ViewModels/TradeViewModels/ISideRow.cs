using KieshStockExchange.Helpers;

namespace KieshStockExchange.ViewModels.TradeViewModels;

public interface ISideRow
{
    bool IsBuyOrder { get; }
    bool IsSellOrder { get; }
}
