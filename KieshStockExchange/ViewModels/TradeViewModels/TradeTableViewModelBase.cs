using CommunityToolkit.Mvvm.ComponentModel;
using KieshStockExchange.Helpers;
using KieshStockExchange.Services.MarketDataServices;
using KieshStockExchange.Services.OtherServices;
using System.Collections.ObjectModel;

namespace KieshStockExchange.ViewModels.TradeViewModels;

public abstract partial class TradeTableViewModelBase<TRow> : StockAwareViewModel
{
    [ObservableProperty] private ObservableCollection<TRow> _currentView = new();

    protected bool ShowAll { get; private set; }

    protected TradeTableViewModelBase(ISelectedStockService selected, INotificationService notification)
        : base(selected, notification) { }

    public void SetShowAll(bool show)
    {
        if (ShowAll == show) return;
        ShowAll = show;
        UpdateFromCache();
    }

    protected sealed override Task OnPriceUpdatedAsync(int? stockId, CurrencyType currency,
        decimal price, DateTime? updatedAt, CancellationToken ct) => Task.CompletedTask;

    protected override Task OnStockChangedAsync(int? stockId, CurrencyType currency, CancellationToken ct)
    {
        UpdateFromCache(stockId, currency);
        return Task.CompletedTask;
    }

    protected void PostUpdateFromCache()
        => MainThread.BeginInvokeOnMainThread(() => UpdateFromCache());

    protected void UpdateFromCache(int? stockId = null, CurrencyType? currency = null)
    {
        if (!Selected.HasSelectedStock)
        {
            CurrentView.Clear();
            return;
        }
        stockId  ??= Selected.StockId;
        currency ??= Selected.Currency;
        UpdateFromCache(stockId!.Value, currency.Value);
    }

    private void UpdateFromCache(int stockId, CurrencyType currency)
    {
        var rows = BuildRows(stockId, currency).ToList();
        OnCurrentViewReplacing(CurrentView);
        CurrentView = new ObservableCollection<TRow>(rows);
    }

    protected abstract IEnumerable<TRow> BuildRows(int stockId, CurrencyType currency);

    protected virtual void OnCurrentViewReplacing(IEnumerable<TRow> oldRows) { }
}
