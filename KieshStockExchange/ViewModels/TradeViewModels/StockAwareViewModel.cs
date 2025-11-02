using KieshStockExchange.Helpers;
using KieshStockExchange.Services;
using KieshStockExchange.ViewModels.OtherViewModels;
using System.ComponentModel;

namespace KieshStockExchange.ViewModels.TradeViewModels;

public abstract class StockAwareViewModel : BaseViewModel, IDisposable
{
    #region Fields, Properties and Constructor
    // Event handler for selected stock changes
    private readonly PropertyChangedEventHandler Handler;
    protected CancellationTokenSource? CtsStock;
    protected CancellationTokenSource? CtsPrice;
    private bool _disposed;

    // Services
    protected readonly ISelectedStockService _selected;
    protected readonly INotificationService _notification;
    public ISelectedStockService Selected => _selected;
    public INotificationService Notification => _notification;

    protected StockAwareViewModel(ISelectedStockService selected, INotificationService notification)
    {
        _selected = selected ?? throw new ArgumentNullException(nameof(selected));
        _notification = notification ?? throw new ArgumentNullException(nameof(notification));
        Handler = OnSelectedChanged;
        _selected.PropertyChanged += Handler;
    }
    #endregion

    #region OnSelectedChanged implementation
    // Callback for when the selected stock or its price changes
    private void OnSelectedChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Update streams if the stock or currency changed
        if (e.PropertyName  is nameof(ISelectedStockService.StockId)
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
            CtsStock?.Cancel(); CtsStock?.Dispose();
            CtsStock = new CancellationTokenSource();
            var ct = CtsStock!.Token;
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
            CtsPrice?.Cancel(); CtsPrice?.Dispose();
            CtsPrice = new CancellationTokenSource();
            var ct = CtsPrice!.Token;
            var stockId = _selected.StockId; var currency = _selected.Currency;
            var price = _selected.CurrentPrice; var updatedAt = _selected.PriceUpdatedAt;
            await OnPriceUpdatedAsync(stockId, currency, price, updatedAt, ct);
        }
        catch (OperationCanceledException) { } // Ignored on cancellation
    }
    #endregion

    #region Abstract Handlers and disposal
    // Initializer to be called by derived classes
    protected void InitializeSelection()
    {
        // Initialize once with whatever is already selected
        FireStockChanged();
        FirePriceChanged();
    }

    // Abstract handlers for derived classes to implement
    protected abstract Task OnStockChangedAsync(int? stockId, CurrencyType currency, CancellationToken ct);

    // Abstract handler for price changes
    protected abstract Task OnPriceUpdatedAsync(int? stockId, CurrencyType currency, 
        decimal price, DateTime? updatedAt, CancellationToken ct);

    protected virtual void Dispose(bool disposing)
    {
        if (_disposed) return;
        if (disposing)
        {
            _selected.PropertyChanged -= Handler;
            CtsStock?.Cancel(); CtsStock?.Dispose();
            CtsPrice?.Cancel(); CtsPrice?.Dispose();
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
