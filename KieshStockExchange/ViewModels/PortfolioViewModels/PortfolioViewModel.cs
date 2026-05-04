using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KieshStockExchange.Helpers;
using KieshStockExchange.Services.MarketDataServices;
using KieshStockExchange.Services.PortfolioServices;
using KieshStockExchange.ViewModels.OtherViewModels;
using Microsoft.Extensions.Logging;
using System.Globalization;

namespace KieshStockExchange.ViewModels.PortfolioViewModels;

public partial class PortfolioViewModel : BaseViewModel
{
    public PortfolioHoldingsViewModel    HoldingsVm     { get; }
    public PortfolioOpenOrdersViewModel  OpenOrdersVm   { get; }
    public PortfolioOrderHistoryViewModel OrderHistoryVm { get; }
    public PortfolioTransactionViewModel TransactionVm  { get; }

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
    private readonly ILogger<PortfolioViewModel> _logger;

    public PortfolioViewModel(
        PortfolioHoldingsViewModel     holdingsVm,
        PortfolioOpenOrdersViewModel   openOrdersVm,
        PortfolioOrderHistoryViewModel orderHistoryVm,
        PortfolioTransactionViewModel  transactionVm,
        IUserPortfolioService          portfolio,
        IMarketDataService             market,
        ILogger<PortfolioViewModel>    logger)
    {
        Title          = "Portfolio";
        HoldingsVm     = holdingsVm     ?? throw new ArgumentNullException(nameof(holdingsVm));
        OpenOrdersVm   = openOrdersVm   ?? throw new ArgumentNullException(nameof(openOrdersVm));
        OrderHistoryVm = orderHistoryVm ?? throw new ArgumentNullException(nameof(orderHistoryVm));
        TransactionVm  = transactionVm  ?? throw new ArgumentNullException(nameof(transactionVm));
        _portfolio     = portfolio      ?? throw new ArgumentNullException(nameof(portfolio));
        _market        = market         ?? throw new ArgumentNullException(nameof(market));
        _logger        = logger         ?? throw new ArgumentNullException(nameof(logger));

        _portfolio.SnapshotChanged += OnPortfolioChanged;
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

    private void RefreshMetrics()
    {
        var cash      = _portfolio.GetBaseFund()?.AvailableBalance ?? 0m;
        var positions = _portfolio.GetPositions();

        var marketValue = 0m;
        foreach (var pos in positions)
        {
            if (_market.Quotes.TryGetValue((pos.StockId, CurrencyType.USD), out var quote))
                marketValue += pos.Quantity * quote.LastPrice;
        }

        var totalEquity = cash + marketValue;
        var count       = positions.Count;

        TotalEquityDisplay       = $"$ {totalEquity.ToString("N2", CultureInfo.InvariantCulture)}";
        TotalEquityChangeDisplay = "—";
        CashDisplay              = $"$ {cash.ToString("N2", CultureInfo.InvariantCulture)}";
        PositionCountDisplay     = count == 1 ? "1 position" : $"{count} positions";

        TodayPlDisplay      = "—";
        TodayPlSubDisplay   = "—";
        AllTimePlDisplay    = "—";
        AllTimePlSubDisplay = "—";
    }
}
