using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KieshStockExchange.Helpers;
using KieshStockExchange.Models;
using KieshStockExchange.Services.MarketDataServices.Interfaces;
using KieshStockExchange.Services.PortfolioServices.Interfaces;
using KieshStockExchange.ViewModels.OtherViewModels;
using Microsoft.Extensions.Logging;
using System.Globalization;

namespace KieshStockExchange.ViewModels.PortfolioViewModels;

public partial class PortfolioViewModel : BaseViewModel, IDisposable
{
    private bool _disposed;

    public PortfolioHoldingsViewModel    HoldingsVm     { get; }
    public PortfolioOpenOrdersViewModel  OpenOrdersVm   { get; }
    public PortfolioOrderHistoryViewModel OrderHistoryVm { get; }
    public PortfolioTransactionViewModel TransactionVm  { get; }
    public TopNavBarViewModel            TopNavBarVm    { get; }

    [ObservableProperty] private string _totalEquityDisplay       = "—";
    [ObservableProperty] private string _totalEquityChangeDisplay = "—";
    [ObservableProperty] private string _cashDisplay              = "—";
    [ObservableProperty] private string _positionCountDisplay     = "—";
    [ObservableProperty] private string _todayPlDisplay           = "—";
    [ObservableProperty] private string _todayPlSubDisplay        = "—";
    [ObservableProperty] private string _allTimePlDisplay         = "—";
    [ObservableProperty] private string _allTimePlSubDisplay      = "—";

    private readonly IUserPortfolioService       _portfolio;
    private readonly IMarketDataService          _market;
    private readonly ITransactionService         _transactions;
    private readonly ILogger<PortfolioViewModel> _logger;

    public PortfolioViewModel(
        PortfolioHoldingsViewModel     holdingsVm,
        PortfolioOpenOrdersViewModel   openOrdersVm,
        PortfolioOrderHistoryViewModel orderHistoryVm,
        PortfolioTransactionViewModel  transactionVm,
        IUserPortfolioService          portfolio,
        IMarketDataService             market,
        ITransactionService            transactions,
        ILogger<PortfolioViewModel>    logger,
        TopNavBarViewModel             topNavBarVm)
    {
        Title          = "Portfolio";
        HoldingsVm     = holdingsVm     ?? throw new ArgumentNullException(nameof(holdingsVm));
        OpenOrdersVm   = openOrdersVm   ?? throw new ArgumentNullException(nameof(openOrdersVm));
        OrderHistoryVm = orderHistoryVm ?? throw new ArgumentNullException(nameof(orderHistoryVm));
        TransactionVm  = transactionVm  ?? throw new ArgumentNullException(nameof(transactionVm));
        _portfolio     = portfolio      ?? throw new ArgumentNullException(nameof(portfolio));
        _market        = market         ?? throw new ArgumentNullException(nameof(market));
        _transactions  = transactions   ?? throw new ArgumentNullException(nameof(transactions));
        _logger        = logger         ?? throw new ArgumentNullException(nameof(logger));
        TopNavBarVm    = topNavBarVm    ?? throw new ArgumentNullException(nameof(topNavBarVm));

        _portfolio.SnapshotChanged    += OnPortfolioChanged;
        _transactions.TransactionsChanged += OnTransactionsChanged;
        RefreshMetrics();
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        try
        {
            await Task.WhenAll(
                HoldingsVm.RefreshCommand.ExecuteAsync(null),
                OpenOrdersVm.RefreshCommand.ExecuteAsync(null),
                OrderHistoryVm.RefreshCommand.ExecuteAsync(null),
                TransactionVm.RefreshCommand.ExecuteAsync(null));

            RefreshMetrics();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to refresh portfolio.");
        }
        finally { IsBusy = false; }
    }

    private void OnPortfolioChanged(object? sender, EventArgs e) =>
        MainThread.BeginInvokeOnMainThread(RefreshMetrics);

    private void OnTransactionsChanged(object? sender, EventArgs e) =>
        MainThread.BeginInvokeOnMainThread(RefreshMetrics);

    public void Dispose()
    {
        if (_disposed) return;
        _portfolio.SnapshotChanged       -= OnPortfolioChanged;
        _transactions.TransactionsChanged -= OnTransactionsChanged;
        TopNavBarVm.Dispose();
        _disposed = true;
        GC.SuppressFinalize(this);
    }

    private void RefreshMetrics()
    {
        var cash      = _portfolio.GetBaseFund()?.AvailableBalance ?? 0m;
        var positions = _portfolio.GetPositions();

        var marketValue = 0m;
        var todayPl     = 0m; // sum over positions of qty * (lastPrice - sessionOpen)

        foreach (var pos in positions)
        {
            if (!_market.Quotes.TryGetValue((pos.StockId, CurrencyType.USD), out var quote)) continue;
            marketValue += pos.Quantity * quote.LastPrice;
            if (quote.Open > 0m)
                todayPl += pos.Quantity * (quote.LastPrice - quote.Open);
        }

        var totalEquity = cash + marketValue;
        var count       = positions.Count;

        // All-time net trading P&L: if you liquidated today at last prices, your net cash
        // position would be marketValue + (cash received from sells) - (cash paid for buys).
        // That removes the need for a separate "deposits" ledger and matches what brokerages
        // call "total return" for the trading account in isolation.
        var allTimePl = ComputeAllTimePl(marketValue);

        TotalEquityDisplay       = FormatUsd(totalEquity);
        TotalEquityChangeDisplay = FormatSignedUsd(todayPl) + " today";
        CashDisplay              = FormatUsd(cash);
        PositionCountDisplay     = count == 1 ? "1 position" : $"{count} positions";

        TodayPlDisplay = FormatSignedUsd(todayPl);
        // Today P/L percent is relative to yesterday's equity (today's open value).
        var todayBase = totalEquity - todayPl;
        TodayPlSubDisplay = todayBase != 0m
            ? FormatSignedPct(todayPl / todayBase * 100m)
            : "—";

        AllTimePlDisplay = FormatSignedUsd(allTimePl);
        // All-time P/L percent is relative to the gross capital deployed (sum of buys).
        var grossDeployed = SumBuyNotional();
        AllTimePlSubDisplay = grossDeployed > 0m
            ? FormatSignedPct(allTimePl / grossDeployed * 100m)
            : "—";
    }

    private decimal ComputeAllTimePl(decimal marketValue)
    {
        decimal sells = 0m;
        var sellList = _transactions.SellTransactions;
        for (int i = 0; i < sellList.Count; i++)
            sells += sellList[i].Quantity * sellList[i].Price;

        decimal buys = SumBuyNotional();

        // Liquidation-equivalent: sells already received + current value of holdings - buys paid.
        return sells + marketValue - buys;
    }

    private decimal SumBuyNotional()
    {
        decimal total = 0m;
        var buyList = _transactions.BuyTransactions;
        for (int i = 0; i < buyList.Count; i++)
            total += buyList[i].Quantity * buyList[i].Price;
        return total;
    }

    private static string FormatUsd(decimal value) =>
        $"$ {value.ToString("N2", CultureInfo.InvariantCulture)}";

    private static string FormatSignedUsd(decimal value)
    {
        var sign = value > 0m ? "+" : value < 0m ? "-" : "";
        return $"{sign}$ {Math.Abs(value).ToString("N2", CultureInfo.InvariantCulture)}";
    }

    private static string FormatSignedPct(decimal value)
    {
        var sign = value > 0m ? "+" : value < 0m ? "-" : "";
        return $"{sign}{Math.Abs(value).ToString("N2", CultureInfo.InvariantCulture)}%";
    }
}
