using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KieshStockExchange.Helpers;
using KieshStockExchange.Services.MarketDataServices.Interfaces;
using KieshStockExchange.Services.OtherServices.Interfaces;
using Microsoft.Extensions.Logging;
using System.Collections.ObjectModel;

namespace KieshStockExchange.ViewModels.TradeViewModels;

public abstract partial class TradeTableViewModelBase<TRow> : StockAwareViewModel
{
    [ObservableProperty] private ObservableCollection<TRow> _currentView = new();

    protected bool ShowAll { get; private set; } = true;

    protected TradeTableViewModelBase(ISelectedStockService selected, INotificationService notification,
        ILogger? logger = null)
        : base(selected, notification, logger) { }

    // The ↗ next to each row's symbol: point the trade page at that row's stock. Shared so all four
    // tables (open orders, history, transactions, positions) navigate identically.
    [RelayCommand]
    protected async Task GoToStockAsync(IStockNav? row)
    {
        if (row is null || row.StockId <= 0) return;
        await Selected.Set(row.StockId, row.Currency);
    }

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
        // SelectedStockService.Set now uses ConfigureAwait(false), so this
        // can land on the threadpool. UpdateFromCache mutates an
        // ObservableCollection (Clear + replace) which is only safe on the
        // UI thread on Windows. Marshal via the existing helper.
        if (MainThread.IsMainThread) UpdateFromCache(stockId, currency);
        else MainThread.BeginInvokeOnMainThread(() => UpdateFromCache(stockId, currency));
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
