using System.ComponentModel;
using KieshStockExchange.Helpers;
using KieshStockExchange.Services;

namespace KieshStockExchange.ViewModels.OtherViewModels;

public abstract class StockAwareViewModel : BaseViewModel, IDisposable
{
    #region Fields, Properties and Constructor
    protected readonly ISelectedStockService Selected;
    private readonly PropertyChangedEventHandler Handler;
    protected CancellationTokenSource? Cts;

    protected StockAwareViewModel(ISelectedStockService selected)
    {
        Selected = selected;
        Handler = OnSelectedChanged;
        Selected.PropertyChanged += Handler;

        // Initialize once with whatever is already selected
        FireStockChanged();
        FirePriceChanged();
    }
    #endregion

    #region OnSelectedChanged implementation
    // Callback for when the selected stock or its price changes
    private void OnSelectedChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Update streams if the stock or currency changed
        if (e.PropertyName  is nameof(ISelectedStockService.StockId)
                            or nameof(ISelectedStockService.SelectedStock)
                            or nameof(ISelectedStockService.Currency))
            FireStockChanged();

        // Notify about price changes
        if (e.PropertyName  is nameof(ISelectedStockService.CurrentPrice)
                            or nameof(ISelectedStockService.PriceUpdatedAt))
            FirePriceChanged();
    }

    private async void FireStockChanged()
    {
        try
        {
            ResetCts();
            var ct = Cts!.Token;
            var stockId = Selected.StockId;
            var currency = Selected.Currency;
            await OnStockChangedAsync(stockId, currency);
        }
        catch (OperationCanceledException) { } // Ignored on cancellation
    }

    private async void FirePriceChanged()
    {
        try
        {
            ResetCts();
            var ct = Cts!.Token;
            var stockId = Selected.StockId; var currency = Selected.Currency;
            var price = Selected.CurrentPrice; var updatedAt = Selected.PriceUpdatedAt;
            await OnPriceChangedAsync(stockId, currency, price, updatedAt);
        }
        catch (OperationCanceledException) { } // Ignored on cancellation
    }

    private void ResetCts()
    {
        Cts?.Cancel();
        Cts?.Dispose();
        Cts = new CancellationTokenSource();
    }
    #endregion

    #region Abstract Handlers and disposal
    // Abstract handlers for derived classes to implement
    protected abstract Task OnStockChangedAsync(int? stockId, CurrencyType currency);

    // Abstract handler for price changes
    protected abstract Task OnPriceChangedAsync(int? stockId, CurrencyType currency, decimal price, DateTime? updatedAt);

    public void Dispose()
    {
        Selected.PropertyChanged -= Handler;
        Cts?.Cancel();
        Cts?.Dispose();
    }
    #endregion
}
