using System.ComponentModel;
using KieshStockExchange.Helpers;
using KieshStockExchange.Services;

namespace KieshStockExchange.ViewModels.OtherViewModels;

public abstract class StockAwareViewModel : BaseViewModel, IDisposable
{
    protected readonly ISelectedStockService Selected;
    private readonly PropertyChangedEventHandler _handler;

    protected StockAwareViewModel(ISelectedStockService selected)
    {
        Selected = selected;
        _handler = OnSelectedChanged;
        Selected.PropertyChanged += _handler;
    }

    // Only respond to stock or currency switches (ignore price ticks)
    private void OnSelectedChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ISelectedStockService.StockId)
            or nameof(ISelectedStockService.SelectedStock)
            or nameof(ISelectedStockService.Currency))
        {
            _ = OnStockChangedAsync(Selected.StockId, Selected.Currency);
        }
    }

    protected abstract Task OnStockChangedAsync(int? stockId, CurrencyType currency);

    public void Dispose() => Selected.PropertyChanged -= _handler;
}
