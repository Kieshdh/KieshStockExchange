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
        await _selected.Set(row.StockId);
        await _selected.ChangeCurrencyAsync(row.Currency);
        await Shell.Current.GoToAsync("///TradePage");
    }

    private void RebuildView()
    {
        var positions = _portfolio.GetPositions().Where(p => p.Quantity > 0).ToList();
        var rows = new List<PositionRow>(positions.Count);

        foreach (var pos in positions)
        {
            if (pos.StockId <= 0) continue;
            rows.Add(CreatePositionRow(pos, CurrencyType.USD));
        }

        rows.Sort((a, b) => b.TotalValue.CompareTo(a.TotalValue));

        var old = CurrentView;
        CurrentView = new ObservableCollection<PositionRow>(rows);
        foreach (var r in old) r.Dispose();
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
