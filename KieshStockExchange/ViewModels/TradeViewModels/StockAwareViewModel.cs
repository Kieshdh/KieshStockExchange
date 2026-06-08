using KieshStockExchange.Helpers;
using KieshStockExchange.Services.MarketDataServices.Interfaces;
using KieshStockExchange.Services.OtherServices.Interfaces;
using KieshStockExchange.ViewModels.OtherViewModels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
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
    protected readonly ILogger _logger;
    public ISelectedStockService Selected => _selected;
    public INotificationService Notification => _notification;

    protected StockAwareViewModel(ISelectedStockService selected, INotificationService notification,
        ILogger? logger = null)
    {
        _selected = selected ?? throw new ArgumentNullException(nameof(selected));
        _notification = notification ?? throw new ArgumentNullException(nameof(notification));
        _logger = logger ?? NullLogger.Instance;
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

        // Notify about price changes. Fire on CurrentPrice too (not just PriceUpdatedAt): a quote
        // can carry a new price under the same LastUpdated timestamp, and the [ObservableProperty]
        // equality check then suppresses the PriceUpdatedAt change — so the chart missed those ticks
        // while the top price (bound to CurrentPrice) updated. RequestRedraw coalesces the extra fires.
        if (e.PropertyName  is nameof(ISelectedStockService.PriceUpdatedAt)
                            or nameof(ISelectedStockService.CurrentPrice))
            FirePriceChanged();
    }

    // SelectedStockService now fires PropertyChanged on the threadpool
    // (see SelectedStockService.Set ConfigureAwait(false) cascade). Each
    // PropertyChanged subscriber gets called in sequence on the same thread,
    // but multiple PropertyChanged events (StockId AND Currency change on
    // the same Set call) can race with each other if delivery happens
    // across different threads. Guard the cancel-and-swap so we never
    // dispose a CTS that another thread just created.
    private readonly object _ctsSwapLock = new();

    private async void FireStockChanged()
    {
        CancellationToken ct;
        int? stockId;
        CurrencyType currency;
        lock (_ctsSwapLock)
        {
            CtsStock?.Cancel();
            CtsStock?.Dispose();
            CtsStock = new CancellationTokenSource();
            ct = CtsStock.Token;
            stockId = _selected.StockId;
            currency = _selected.Currency;
        }
        try
        {
            await OnStockChangedAsync(stockId, currency, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { } // Ignored on cancellation
        catch (Exception ex)
        {
            // Swallow to keep the SynchronizationContext from tearing down the app.
            _logger.LogError(ex, "{ViewModel}: OnStockChangedAsync failed.", GetType().Name);
        }
    }

    private async void FirePriceChanged()
    {
        CancellationToken ct;
        int? stockId;
        CurrencyType currency;
        decimal price;
        DateTime? updatedAt;
        lock (_ctsSwapLock)
        {
            CtsPrice?.Cancel();
            CtsPrice?.Dispose();
            CtsPrice = new CancellationTokenSource();
            ct = CtsPrice.Token;
            stockId = _selected.StockId;
            currency = _selected.Currency;
            price = _selected.CurrentPrice;
            updatedAt = _selected.PriceUpdatedAt;
        }
        try
        {
            await OnPriceUpdatedAsync(stockId, currency, price, updatedAt, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { } // Ignored on cancellation
        catch (Exception ex)
        {
            _logger.LogError(ex, "{ViewModel}: OnPriceUpdatedAsync failed.", GetType().Name);
        }
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
            lock (_ctsSwapLock)
            {
                CtsStock?.Cancel();
                CtsStock?.Dispose();
                CtsStock = null;
                CtsPrice?.Cancel();
                CtsPrice?.Dispose();
                CtsPrice = null;
            }
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
