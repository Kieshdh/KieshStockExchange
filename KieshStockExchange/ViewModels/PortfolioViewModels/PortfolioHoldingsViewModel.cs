using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KieshStockExchange.Helpers;
using KieshStockExchange.Models;
using KieshStockExchange.Services.DataServices.Interfaces;
using KieshStockExchange.Services.MarketDataServices.Interfaces;
using KieshStockExchange.Services.BackgroundServices.Interfaces;
using KieshStockExchange.Services.PortfolioServices.Interfaces;
using KieshStockExchange.Services.UserServices.Interfaces;
using KieshStockExchange.ViewModels.OtherViewModels;
using KieshStockExchange.ViewModels.TradeViewModels;
using Microsoft.Extensions.Logging;
using System.Collections.ObjectModel;

namespace KieshStockExchange.ViewModels.PortfolioViewModels;

public partial class PortfolioHoldingsViewModel : BaseViewModel, IDisposable
{
    private bool _disposed;
    private readonly HashSet<(int StockId, CurrencyType Currency)> _subscriptions = new();
    private readonly Dictionary<int, PositionRow> _rowsByStockId = new();

    private readonly IUserPortfolioService _portfolio;
    private readonly IMarketDataService    _market;
    private readonly IStockService         _stocks;
    private readonly IFxRateService        _fx;
    private readonly IUserSessionService   _session;
    private readonly IAuthService          _auth;
    private readonly ISelectedStockService _selected;
    private readonly ILogger<PortfolioHoldingsViewModel> _logger;

    public ClientPager<PositionRow> Pager { get; } = new();

    public PortfolioHoldingsViewModel(
        IUserPortfolioService portfolio,
        IMarketDataService    market,
        IStockService         stocks,
        IFxRateService        fx,
        IUserSessionService   session,
        IAuthService          auth,
        ISelectedStockService selected,
        ILogger<PortfolioHoldingsViewModel> logger)
    {
        _portfolio = portfolio ?? throw new ArgumentNullException(nameof(portfolio));
        _market    = market    ?? throw new ArgumentNullException(nameof(market));
        _stocks    = stocks    ?? throw new ArgumentNullException(nameof(stocks));
        _fx        = fx        ?? throw new ArgumentNullException(nameof(fx));
        _session   = session   ?? throw new ArgumentNullException(nameof(session));
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

    // Maintain _rowsByStockId in place so live-quote INPC updates the same
    // PositionRow instances across rebuilds (no per-tick churn). The pager
    // slices the sorted snapshot into PagedItems for the bound CollectionView.
    private void RebuildView()
    {
        var positions = _portfolio.GetPositions().Where(p => p.Quantity > 0).ToList();
        var present = new HashSet<int>(positions.Count);
        foreach (var p in positions) if (p.StockId > 0) present.Add(p.StockId);

        var stale = _rowsByStockId.Keys.Where(id => !present.Contains(id)).ToList();
        foreach (var id in stale)
        {
            if (_rowsByStockId.Remove(id, out var row)) row.Dispose();
        }

        foreach (var pos in positions)
        {
            if (pos.StockId <= 0) continue;
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
                _rowsByStockId[pos.StockId] = CreatePositionRow(pos, ccy);
            }
        }

        var sorted = _rowsByStockId.Values
            .OrderByDescending(r => r.TotalValue)
            .ToList();

        RefreshDepthRatios(sorted);
        Pager.SetSource(sorted);
    }

    // Total denominator includes cash so the bar lengths line up with the
    // Currencies tab — they sum to 100% across both tabs together.
    private void RefreshDepthRatios(IReadOnlyList<PositionRow> rows)
    {
        var baseCcy = _session.BaseCurrency;
        var total = PortfolioTotalsHelper.TotalInBase(_portfolio, _market, _stocks, _fx, baseCcy);
        if (total <= 0m) return;
        foreach (var row in rows)
        {
            var inBase = PortfolioTotalsHelper.ConvertViaFx(_fx, row.TotalValue, row.Currency, baseCcy);
            row.DepthRatio = (double)(inBase / total);
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
            TradeCommand = TradeCommand,
        };
    }

    private void OnPositionsChanged(object? sender, EventArgs e)
    {
        try { MainThread.BeginInvokeOnMainThread(RebuildView); }
        catch (Exception ex) { _logger.LogError(ex, "Error updating portfolio holdings."); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _portfolio.SnapshotChanged -= OnPositionsChanged;
        // Position rows wired into LiveQuote.PropertyChanged need to release
        // those handlers too, otherwise quote ticks keep firing into a stale
        // row collection after this VM is gone.
        foreach (var row in _rowsByStockId.Values) row.Dispose();
        _rowsByStockId.Clear();
        GC.SuppressFinalize(this);
    }
}
