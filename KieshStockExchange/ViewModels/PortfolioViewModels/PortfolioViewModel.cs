using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KieshStockExchange.Helpers;
using KieshStockExchange.Models;
using KieshStockExchange.Services.BackgroundServices.Interfaces;
using KieshStockExchange.Services.DataServices.Interfaces;
using KieshStockExchange.Services.MarketDataServices.Interfaces;
using KieshStockExchange.Services.PortfolioServices.Interfaces;
using KieshStockExchange.ViewModels.OtherViewModels;
using Microsoft.Extensions.Logging;

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
    private readonly IStockService               _stocks;
    private readonly IFxRateService              _fxRates;
    private readonly IUserSessionService         _session;
    private readonly ILogger<PortfolioViewModel> _logger;

    public PortfolioViewModel(
        PortfolioHoldingsViewModel     holdingsVm,
        PortfolioOpenOrdersViewModel   openOrdersVm,
        PortfolioOrderHistoryViewModel orderHistoryVm,
        PortfolioTransactionViewModel  transactionVm,
        IUserPortfolioService          portfolio,
        IMarketDataService             market,
        ITransactionService            transactions,
        IStockService                  stocks,
        IFxRateService                 fxRates,
        IUserSessionService            session,
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
        _stocks        = stocks         ?? throw new ArgumentNullException(nameof(stocks));
        _fxRates       = fxRates        ?? throw new ArgumentNullException(nameof(fxRates));
        _session       = session        ?? throw new ArgumentNullException(nameof(session));
        _logger        = logger         ?? throw new ArgumentNullException(nameof(logger));
        TopNavBarVm    = topNavBarVm    ?? throw new ArgumentNullException(nameof(topNavBarVm));

        _portfolio.SnapshotChanged    += OnPortfolioChanged;
        _transactions.TransactionsChanged += OnTransactionsChanged;
        _session.SnapshotChanged      += OnSessionChanged;
        RefreshMetrics();
    }

    // 3.2 Phase B: route P&L cross-currency walks through the live FX mid
    // instead of the static rate table so the displayed numbers move with
    // the AR(1) drift. Rounding happens at the target currency's precision
    // (mirrors CurrencyHelper.ConvertMoney).
    private decimal ConvertViaFx(decimal amount, CurrencyType from, CurrencyType to)
    {
        if (from == to) return CurrencyHelper.RoundMoney(amount, to);
        var mid = _fxRates.GetMidRate(from, to);
        return CurrencyHelper.RoundMoney(amount * mid, to);
    }

    private void OnSessionChanged(object? sender, SessionSnapshot e) =>
        MainThread.BeginInvokeOnMainThread(RefreshMetrics);

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
        _session.SnapshotChanged         -= OnSessionChanged;
        TopNavBarVm.Dispose();
        _disposed = true;
        GC.SuppressFinalize(this);
    }

    private void RefreshMetrics()
    {
        var baseCcy = _session.BaseCurrency;

        // Aggregate cash across all fund rows. Funds live in the user's own currencies,
        // so each row is converted into the session's BaseCurrency before summing.
        decimal cash = 0m;
        foreach (var f in _portfolio.GetFunds())
            cash += ConvertViaFx(f.AvailableBalance, f.CurrencyType, baseCcy);

        var positions = _portfolio.GetPositions();

        var marketValue = 0m;
        var todayPl     = 0m; // sum over positions of qty * (lastPrice - sessionOpen)

        foreach (var pos in positions)
        {
            if (!_stocks.TryGetCurrency(pos.StockId, out var ccy)) continue;
            if (!_market.Quotes.TryGetValue((pos.StockId, ccy), out var quote)) continue;

            var posValueLocal = CurrencyHelper.Notional(quote.LastPrice, pos.Quantity, ccy);
            marketValue += ConvertViaFx(posValueLocal, ccy, baseCcy);
            if (quote.Open > 0m)
            {
                var todayDeltaLocal = CurrencyHelper.Notional(quote.LastPrice - quote.Open, pos.Quantity, ccy);
                todayPl += ConvertViaFx(todayDeltaLocal, ccy, baseCcy);
            }
        }

        var totalEquity = cash + marketValue;
        var count       = positions.Count;

        // All-time net trading P&L: if you liquidated today at last prices, your net cash
        // position would be marketValue + (cash received from sells) - (cash paid for buys).
        // That removes the need for a separate "deposits" ledger and matches what brokerages
        // call "total return" for the trading account in isolation.
        var allTimePl = ComputeAllTimePl(marketValue, baseCcy);

        TotalEquityDisplay       = CurrencyHelper.Format(totalEquity, baseCcy);
        TotalEquityChangeDisplay = FormatSigned(todayPl, baseCcy) + " today";
        CashDisplay              = CurrencyHelper.Format(cash, baseCcy);
        PositionCountDisplay     = count == 1 ? "1 position" : $"{count} positions";

        TodayPlDisplay = FormatSigned(todayPl, baseCcy);
        // Today P/L percent is relative to yesterday's equity (today's open value).
        var todayBase = totalEquity - todayPl;
        TodayPlSubDisplay = todayBase != 0m
            ? FormatSignedPct(todayPl / todayBase * 100m)
            : "—";

        AllTimePlDisplay = FormatSigned(allTimePl, baseCcy);
        // All-time P/L percent is relative to the gross capital deployed (sum of buys).
        var grossDeployed = SumBuyNotional(baseCcy);
        AllTimePlSubDisplay = grossDeployed > 0m
            ? FormatSignedPct(allTimePl / grossDeployed * 100m)
            : "—";
    }

    private decimal ComputeAllTimePl(decimal marketValue, CurrencyType baseCcy)
    {
        decimal sells = 0m;
        var sellList = _transactions.SellTransactions;
        for (int i = 0; i < sellList.Count; i++)
        {
            var t = sellList[i];
            sells += ConvertViaFx(
                CurrencyHelper.Notional(t.Price, t.Quantity, t.CurrencyType), t.CurrencyType, baseCcy);
        }

        decimal buys = SumBuyNotional(baseCcy);

        // Liquidation-equivalent: sells already received + current value of holdings - buys paid.
        return sells + marketValue - buys;
    }

    private decimal SumBuyNotional(CurrencyType baseCcy)
    {
        decimal total = 0m;
        var buyList = _transactions.BuyTransactions;
        for (int i = 0; i < buyList.Count; i++)
        {
            var t = buyList[i];
            total += ConvertViaFx(
                CurrencyHelper.Notional(t.Price, t.Quantity, t.CurrencyType), t.CurrencyType, baseCcy);
        }
        return total;
    }

    private static string FormatSigned(decimal value, CurrencyType currency)
    {
        var formatted = CurrencyHelper.Format(Math.Abs(value), currency);
        var sign = value > 0m ? "+" : value < 0m ? "-" : "";
        return $"{sign}{formatted}";
    }

    private static string FormatSignedPct(decimal value)
    {
        var sign = value > 0m ? "+" : value < 0m ? "-" : "";
        return $"{sign}{Math.Abs(value).ToString("N2", System.Globalization.CultureInfo.InvariantCulture)}%";
    }
}
