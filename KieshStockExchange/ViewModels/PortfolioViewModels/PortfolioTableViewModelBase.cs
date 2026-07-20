using CommunityToolkit.Mvvm.Input;
using KieshStockExchange.ViewModels.OtherViewModels;
using Microsoft.Extensions.Logging;

namespace KieshStockExchange.ViewModels.PortfolioViewModels;

/// <summary>
/// Shared skeleton for the paged portfolio tables (open orders, order history,
/// transactions): busy-guarded refresh, StockId-filtered newest-first rebuild,
/// UI-thread marshaling on a service event, and unsubscribe-on-dispose.
/// Subclasses supply the source, the sort key, the row projection, the async
/// service refresh, and the event wiring.
/// </summary>
public abstract partial class PortfolioTableViewModelBase<TRow, TSource> : BaseViewModel, IDisposable
{
    private bool _disposed;
    // Typed logger passed up by each subclass so LogError keeps its category
    protected readonly ILogger _logger;

    public ClientPager<TRow> Pager { get; } = new();

    protected PortfolioTableViewModelBase(ILogger logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    // --- Per-table hooks ---------------------------------------------------
    // Live source read on every rebuild (no await — the event path never refreshes)
    protected abstract IEnumerable<TSource> Source { get; }
    protected abstract int GetStockId(TSource item);
    protected abstract DateTime GetSortKey(TSource item);
    protected abstract TRow CreateRow(TSource item);
    // Awaited by RefreshAsync before the rebuild; the event path skips it
    protected abstract Task RefreshSourceAsync();
    protected abstract void Subscribe();
    protected abstract void Unsubscribe();
    protected abstract string RefreshErrorMessage { get; }
    protected abstract string UpdateErrorMessage { get; }

    [RelayCommand]
    public async Task RefreshAsync()
    {
        await RunBusyAsync(async () =>
        {
            await RefreshSourceAsync();
            RebuildView();
        }, ex => _logger.LogError(ex, RefreshErrorMessage));
    }

    // Re-slice the current source into the pager (real stocks only, newest first)
    protected void RebuildView()
    {
        var rows = Source
            .Where(item => GetStockId(item) > 0)
            .OrderByDescending(GetSortKey)
            .Select(CreateRow)
            .ToList();

        Pager.SetSource(rows);
    }

    // Service fired off-thread → rebuild on the UI thread
    protected void OnSourceChanged(object? sender, EventArgs e)
    {
        try { MainThread.BeginInvokeOnMainThread(RebuildView); }
        catch (Exception ex) { _logger.LogError(ex, UpdateErrorMessage); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Unsubscribe();
        GC.SuppressFinalize(this);
    }
}
