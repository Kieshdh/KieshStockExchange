using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KieshStockExchange.Helpers;
using KieshStockExchange.Models;
using KieshStockExchange.Services.DataServices.Interfaces;
using KieshStockExchange.Services.MarketDataServices.Interfaces;
using KieshStockExchange.Services.PortfolioServices.Interfaces;
using KieshStockExchange.Services.UserServices.Interfaces;
using KieshStockExchange.ViewModels.OtherViewModels;
using KieshStockExchange.ViewModels.TradeViewModels;
using Microsoft.Extensions.Logging;
using System.Collections.ObjectModel;

namespace KieshStockExchange.ViewModels.PortfolioViewModels;

public partial class PortfolioHoldingsViewModel : BaseViewModel
{
    private readonly HashSet<(int StockId, CurrencyType Currency)> _subscriptions = new();
    private readonly Dictionary<int, PositionRow> _rowsByStockId = new();

    private readonly IUserPortfolioService _portfolio;
    private readonly IMarketDataService    _market;
    private readonly IStockService         _stocks;
    private readonly IAuthService          _auth;
    private readonly ISelectedStockService _selected;
    private readonly ILogger<PortfolioHoldingsViewModel> _logger;

    [ObservableProperty] private ObservableCollection<PositionRow> _currentView = new();

    public PortfolioHoldingsViewModel(
        IUserPortfolioService portfolio,
        IMarketDataService    market,
        IStockService         stocks,
        IAuthService          auth,
        ISelectedStockService selected,
        ILogger<PortfolioHoldingsViewModel> logger)
    {
        _portfolio = portfolio ?? throw new ArgumentNullException(nameof(portfolio));
        _market    = market    ?? throw new ArgumentNullException(nameof(market));
        _stocks    = stocks    ?? throw new ArgumentNullException(nameof(stocks));
        _auth      = auth      ?? throw new ArgumentNullException(nameof(auth));
        _selected  = selected  ?? throw new ArgumentNullException(nameof(selected));
        _logger    = logger    ?? throw new ArgumentNullException(nameof(logger));

        _portfolio.SnapshotChanged += OnPositionsChanged;
    }

    [RelayCommand]
    public async Task RefreshAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        try
        {
            await _portfolio.RefreshAsync(_auth.CurrentUserId);
            RebuildView();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error refreshing portfolio holdings.");
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    public async Task TradeAsync(PositionRow? row)
    {
        if (row is null) return;
        await _selected.Set(row.StockId, row.Currency);
        await Shell.Current.GoToAsync("///TradePage");
    }

    // Sync CurrentView in place rather than swapping the ObservableCollection
    // reference. Replacing the collection causes the bound CollectionView to
    // tear down and rebuild every row, producing a brief blank frame on every
    // SnapshotChanged event. Now we mutate row state, add new positions, and
    // remove closed-out positions individually so untouched rows do not flicker.
    private void RebuildView()
    {
        var positions = _portfolio.GetPositions().Where(p => p.Quantity > 0).ToList();
        var present = new HashSet<int>(positions.Count);
        foreach (var p in positions) if (p.StockId > 0) present.Add(p.StockId);

        // Remove rows whose positions disappeared (closed out / quantity → 0).
        var stale = _rowsByStockId.Keys.Where(id => !present.Contains(id)).ToList();
        foreach (var id in stale)
        {
            if (_rowsByStockId.Remove(id, out var row))
            {
                CurrentView.Remove(row);
                row.Dispose();
            }
        }

        // Add or refresh rows for each present position. Insert new rows in
        // sorted position (TotalValue desc) so the initial order is correct;
        // existing rows are not reordered on value drift to avoid pointless
        // moves. The user can hit Refresh for a hard re-sort if needed.
        foreach (var pos in positions)
        {
            if (pos.StockId <= 0) continue;

            // Each position trades in its stock's listed currency, so subscribe
            // to and read the quote for that (stockId, currency) pair instead of
            // the USD-only legacy. Falls back to USD if the catalog hasn't loaded.
            if (!_stocks.TryGetCurrency(pos.StockId, out var ccy))
                ccy = CurrencyType.USD;

            if (_rowsByStockId.TryGetValue(pos.StockId, out var existing))
            {
                if (_market.Quotes.TryGetValue((pos.StockId, ccy), out var live)
                    && !ReferenceEquals(existing.Live, live))
                {
                    existing.Live = live;
                }
                existing.RefreshPositionFields();
            }
            else
            {
                var row = CreatePositionRow(pos, ccy);
                _rowsByStockId[pos.StockId] = row;

                int idx = 0;
                while (idx < CurrentView.Count && CurrentView[idx].TotalValue >= row.TotalValue)
                    idx++;
                CurrentView.Insert(idx, row);
            }
        }
    }

    private PositionRow CreatePositionRow(Position pos, CurrencyType currency)
    {
        if (!_stocks.TryGetSymbol(pos.StockId, out string symbol))
            symbol = "-";

        var key = (pos.StockId, currency);
        if (_subscriptions.Add(key))
        {
            _ = _market.SubscribeAsync(pos.StockId, currency);
            _ = _market.BuildFromHistoryAsync(pos.StockId, currency);
        }

        _market.Quotes.TryGetValue((pos.StockId, currency), out var live);

        return new PositionRow
        {
            Symbol   = symbol,
            Currency = currency,
            Live     = live,
            Pos      = pos,
        };
    }

    private void OnPositionsChanged(object? sender, EventArgs e)
    {
        try { MainThread.BeginInvokeOnMainThread(RebuildView); }
        catch (Exception ex) { _logger.LogError(ex, "Error updating portfolio holdings."); }
    }
}
