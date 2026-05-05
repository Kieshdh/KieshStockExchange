using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KieshStockExchange.Helpers;
using KieshStockExchange.Services.MarketDataServices.Interfaces;
using KieshStockExchange.Services.PortfolioServices.Interfaces;
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
    public TopNavBarViewModel            TopNavBarVm    { get; }

    [ObservableProperty] private string _totalEquityDisplay       = "Ã¢â‚¬â€";
    [ObservableProperty] private string _totalEquityChangeDisplay = "Ã¢â‚¬â€";
    [ObservableProperty] private string _cashDisplay              = "Ã¢â‚¬â€";
    [ObservableProperty] private string _positionCountDisplay     = "Ã¢â‚¬â€";
    [ObservableProperty] private string _todayPlDisplay           = "Ã¢â‚¬â€";
    [ObservableProperty] private string _todayPlSubDisplay        = "Ã¢â‚¬â€";
    [ObservableProperty] private string _allTimePlDisplay         = "Ã¢â‚¬â€";
    [ObservableProperty] private string _allTimePlSubDisplay      = "Ã¢â‚¬â€";

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
        _logger        = logger         ?? throw new ArgumentNullException(nameof(logger));
        TopNavBarVm    = topNavBarVm    ?? throw new ArgumentNullException(nameof(topNavBarVm));

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
        TotalEquityChangeDisplay = "Ã¢â‚¬â€";
        CashDisplay              = $"$ {cash.ToString("N2", CultureInfo.InvariantCulture)}";
        PositionCountDisplay     = count == 1 ? "1 position" : $"{count} positions";

        TodayPlDisplay      = "Ã¢â‚¬â€";
        TodayPlSubDisplay   = "Ã¢â‚¬â€";
        AllTimePlDisplay    = "Ã¢â‚¬â€";
        AllTimePlSubDisplay = "Ã¢â‚¬â€";
    }
}
