using KieshStockExchange.Helpers;
using KieshStockExchange.Services;
using KieshStockExchange.ViewModels.OtherViewModels;
using System.ComponentModel;
using System.Reflection.Metadata;

namespace KieshStockExchange.ViewModels.TradeViewModels;

public abstract class StockAwareViewModel : BaseViewModel, IDisposable
{
    #region Fields, Properties and Constructor
    protected readonly ISelectedStockService _selected;
    public ISelectedStockService Selected => _selected;
    private readonly PropertyChangedEventHandler Handler;
    protected CancellationTokenSource? Cts;
    private bool _disposed;

    protected StockAwareViewModel(ISelectedStockService selected)
    {
        _selected = selected ?? throw new ArgumentNullException(nameof(selected));
        Handler = OnSelectedChanged;
        _selected.PropertyChanged += Handler;
    }

    protected void InitializeSelection()
    {
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
        if (e.PropertyName  is nameof(ISelectedStockService.HasSelectedStock)
                            or nameof(ISelectedStockService.Currency))
            FireStockChanged();

        // Notify about price changes
        if (e.PropertyName  is nameof(ISelectedStockService.PriceUpdatedAt))
                            //or nameof(ISelectedStockService.CurrentPrice))
            FirePriceChanged();
    }

    private async void FireStockChanged()
    {
        try
        {
            ResetCts();
            var ct = Cts!.Token;
            var stockId = _selected.StockId;
            var currency = _selected.Currency;
            await OnStockChangedAsync(stockId, currency, ct);
        }
        catch (OperationCanceledException) { } // Ignored on cancellation
    }

    private async void FirePriceChanged()
    {
        try
        {
            ResetCts();
            var ct = Cts!.Token;
            var stockId = _selected.StockId; var currency = _selected.Currency;
            var price = _selected.CurrentPrice; var updatedAt = _selected.PriceUpdatedAt;
            await OnPriceUpdatedsync(stockId, currency, price, updatedAt, ct);
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
    protected abstract Task OnStockChangedAsync(int? stockId, CurrencyType currency, CancellationToken ct);

    // Abstract handler for price changes
    protected abstract Task OnPriceUpdatedsync(int? stockId, CurrencyType currency, 
        decimal price, DateTime? updatedAt, CancellationToken ct);

    protected virtual void Dispose(bool disposing)
    {
        if (_disposed) return;
        if (disposing)
        {
            _selected.PropertyChanged -= Handler;
            Cts?.Cancel();
            Cts?.Dispose();
        }
        _disposed = true;
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }
    #endregion
}
