using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KieshStockExchange.Helpers;
using KieshStockExchange.Models;
using KieshStockExchange.Services.DataServices.Interfaces;
using KieshStockExchange.Services.MarketDataServices;
using KieshStockExchange.Services.MarketDataServices.Interfaces;
using KieshStockExchange.Services.OtherServices.Interfaces;
using KieshStockExchange.Services.PortfolioServices.Interfaces;
using KieshStockExchange.Services.UserServices.Interfaces;
using Microsoft.Extensions.Logging;
using System.ComponentModel;
using System.Windows.Input;

namespace KieshStockExchange.ViewModels.TradeViewModels;

public sealed partial class PositionRow : ObservableObject, IDisposable, IStockNav
{
    #region Initialization Properties
    public required Position Pos { get; init; }
    public required string Symbol { get; init; }
    public required CurrencyType Currency { get; init; }
    // Trade page: the ↗ (go-to-stock) glyph. Optional because the Portfolio page reuses this row
    // with TradeCommand instead.
    public ICommand? GoToStockCommand { get; init; }
    // Portfolio page: select the stock AND navigate to the Trade page (Shell). Distinct from the
    // trade page's in-page GoToStock.
    public ICommand? TradeCommand { get; init; }
    #endregion

    #region Live Data Property
    [ObservableProperty] private LiveQuote? _live;

    // Set by the holdings VM after a portfolio rebuild — share of total
    // portfolio value (cash + open positions) in base currency. Used by
    // the share-bar column in the Holdings table.
    [ObservableProperty] private double _depthRatio;

    private bool _disposed;
    public int StockId => Pos.StockId;
    public decimal CurrentPrice => Live?.LastPrice ?? 0m;
    public decimal TotalValue => CurrentPrice > 0m ? CurrencyHelper.Notional(CurrentPrice, Pos.Quantity, Currency) : 0m;
    #endregion

    #region Formatted Properties
    public bool IsShort => Pos.Quantity < 0;
    public string Price => CurrentPrice <= 0m ? "-" : CurrencyHelper.Format(CurrentPrice, Currency);
    // Shorts read as "SHORT N" so a negative balance is unmistakable in the table.
    public string Qty => IsShort ? $"SHORT {-Pos.Quantity}" : Pos.Quantity.ToString();
    public string Reserved => IsShort
        ? CurrencyHelper.Format(Pos.ShortCollateral, Pos.ShortCollateralCurrency)
        : Pos.ReservedQuantity.ToString();
    public string Available => Pos.AvailableQuantity.ToString();
    public string Total => CurrencyHelper.Format(TotalValue, Currency);
    #endregion

    /// <summary>
    /// Re-fire change notifications for getters derived from <see cref="Pos"/>.
    /// Call after the portfolio service has mutated the cached Position's Quantity
    /// or ReservedQuantity so bindings (Qty, Reserved, Available, Total) refresh
    /// in place rather than the row being torn down and re-created.
    /// </summary>
    public void RefreshPositionFields()
    {
        if (_disposed) return;
        OnPropertyChanged(nameof(Qty));
        OnPropertyChanged(nameof(Reserved));
        OnPropertyChanged(nameof(Available));
        OnPropertyChanged(nameof(Total));
    }

    #region Live Quote Change Handler and Disposal
    partial void OnLiveChanged(LiveQuote? oldValue, LiveQuote? newValue)
    {
        if (_disposed) return;
        if (oldValue == newValue) return;

        if (oldValue is not null)
            oldValue.PropertyChanged -= OnLivePropertyChanged;
        if (newValue is not null)
            newValue.PropertyChanged += OnLivePropertyChanged;

        OnPropertyChanged(nameof(Price));
        OnPropertyChanged(nameof(Total));
    }

    private void OnLivePropertyChanged(object? s, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not nameof(LiveQuote.LastPrice)) return;

        // LiveQuote raises PropertyChanged from TickPipeline.ApplyTick, which
        // runs on a thread-pool reader thread when the book has no UI
        // subscribers. Without this marshal, our own OnPropertyChanged would
        // propagate on the bg thread and crash any UI binding on Price/Total.
        MainThread.BeginInvokeOnMainThread(() =>
        {
            if (_disposed) return;
            try
            {
                OnPropertyChanged(nameof(Price));
                OnPropertyChanged(nameof(Total));
            }
            catch (System.Runtime.InteropServices.COMException)
            {
                // Shutdown race: WinUI peer torn down between queue and dispatch.
            }
        });
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (Live is not null)
            Live.PropertyChanged -= OnLivePropertyChanged;
        Live = null;
        GC.SuppressFinalize(this);
    }
    #endregion
}
