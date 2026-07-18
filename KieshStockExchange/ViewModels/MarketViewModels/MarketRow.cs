using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KieshStockExchange.Helpers;
using KieshStockExchange.Services.BackgroundServices.Interfaces;
using KieshStockExchange.Services.DataServices.Interfaces;
using KieshStockExchange.Services.MarketDataServices;
using KieshStockExchange.Services.MarketDataServices.Interfaces;
using KieshStockExchange.Services.PortfolioServices.Interfaces;
using KieshStockExchange.ViewModels.OtherViewModels;
using Microsoft.Extensions.Logging;
using System.Collections.ObjectModel;
using System.Windows.Input;

namespace KieshStockExchange.ViewModels.MarketViewModels;

/// <summary> Row bound by the All-Stocks table. Identity is (StockId, Currency). </summary>
public partial class MarketRow : ObservableObject
{
    public required int StockId { get; init; }
    public required string Symbol { get; init; }
    public required string CompanyName { get; init; }
    public required CurrencyType Currency { get; init; }
    // Injected by owner VM so Trade button binds directly.
    public required ICommand TradeCommand { get; init; }
    public required ICommand ToggleWatchCommand { get; init; }

    [ObservableProperty] private string _lastPriceDisplay = "-";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBullish))]
    [NotifyPropertyChangedFor(nameof(IsBearish))]
    private decimal _changePct;

    [ObservableProperty] private string _changePctDisplay = "-";

    [ObservableProperty] private string _dollarVolumeDisplay = "-";

    // Numeric session $-volume, kept beside the display string as the sort key for
    // the Volume column (not bound to the UI).
    public decimal DollarVolume { get; set; }

    [ObservableProperty] private bool _isWatched;

    public bool IsBullish => ChangePct > 0m;
    public bool IsBearish => ChangePct < 0m;

    public static MarketRow FromQuote(LiveQuote q, ICommand tradeCommand, ICommand toggleWatchCommand, bool isWatched) => new()
    {
        StockId             = q.StockId,
        Symbol              = q.Symbol,
        CompanyName         = q.CompanyName,
        Currency            = q.Currency,
        LastPriceDisplay    = q.LastPriceDisplay,
        ChangePct           = q.ChangePct,
        ChangePctDisplay    = q.ChangePctDisplay,
        DollarVolumeDisplay = q.DollarVolumeDisplay,
        DollarVolume        = q.DollarVolume,
        TradeCommand        = tradeCommand,
        ToggleWatchCommand  = toggleWatchCommand,
        IsWatched           = isWatched,
    };

    public void UpdateFrom(LiveQuote q)
    {
        LastPriceDisplay     = q.LastPriceDisplay;
        ChangePct            = q.ChangePct;
        ChangePctDisplay     = q.ChangePctDisplay;
        DollarVolumeDisplay  = q.DollarVolumeDisplay;
        DollarVolume         = q.DollarVolume;
    }
}
