using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KieshStockExchange.Helpers;
using KieshStockExchange.Models;
using KieshStockExchange.Services.BackgroundServices.Interfaces;
using KieshStockExchange.Services.MarketDataServices.Interfaces;
using KieshStockExchange.Services.PortfolioServices.Interfaces;
using KieshStockExchange.Services.UserServices.Interfaces;
using KieshStockExchange.ViewModels.OtherViewModels;
using Microsoft.Extensions.Logging;
using System.Collections.ObjectModel;

namespace KieshStockExchange.ViewModels.PortfolioViewModels;

/// <summary>
/// Portfolio "Currencies" tab — one row per Fund with native balance, the
/// equivalent value in the session base currency, and a relative-share bar.
/// </summary>
public partial class PortfolioCurrenciesViewModel : BaseViewModel
{
    private readonly IUserPortfolioService _portfolio;
    private readonly IFxRateService _fx;
    private readonly IUserSessionService _session;
    private readonly IAuthService _auth;
    private readonly ILogger<PortfolioCurrenciesViewModel> _logger;

    [ObservableProperty] private ObservableCollection<CurrencyRow> _currentView = new();
    [ObservableProperty] private bool _hideZeroBalances = true;
    [ObservableProperty] private string _totalDisplay = "—";
    [ObservableProperty] private string _baseCurrencyDisplay = "USD";

    public PortfolioCurrenciesViewModel(
        IUserPortfolioService portfolio,
        IFxRateService fx,
        IUserSessionService session,
        IAuthService auth,
        ILogger<PortfolioCurrenciesViewModel> logger)
    {
        _portfolio = portfolio ?? throw new ArgumentNullException(nameof(portfolio));
        _fx        = fx        ?? throw new ArgumentNullException(nameof(fx));
        _session   = session   ?? throw new ArgumentNullException(nameof(session));
        _auth      = auth      ?? throw new ArgumentNullException(nameof(auth));
        _logger    = logger    ?? throw new ArgumentNullException(nameof(logger));

        _portfolio.SnapshotChanged += OnPortfolioChanged;
        _session.SnapshotChanged   += OnSessionChanged;
    }

    [RelayCommand]
    public async Task RefreshAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        try
        {
            await _portfolio.RefreshAsync(_auth.CurrentUserId).ConfigureAwait(false);
            await MainThread.InvokeOnMainThreadAsync(RebuildView);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error refreshing portfolio currencies.");
        }
        finally { IsBusy = false; }
    }

    partial void OnHideZeroBalancesChanged(bool value) => RebuildView();

    private void OnPortfolioChanged(object? sender, EventArgs e) =>
        MainThread.BeginInvokeOnMainThread(RebuildView);

    private void OnSessionChanged(object? sender, SessionSnapshot e) =>
        MainThread.BeginInvokeOnMainThread(RebuildView);

    private void RebuildView()
    {
        var baseCcy = _session.BaseCurrency;
        BaseCurrencyDisplay = CurrencyHelper.GetIsoCode(baseCcy);

        var funds = _portfolio.GetFunds();
        var rows = new List<CurrencyRow>(funds.Count);

        decimal total = 0m;
        decimal maxValue = 0m;

        foreach (var f in funds)
        {
            if (HideZeroBalances && f.TotalBalance <= 0m && f.ReservedBalance <= 0m) continue;

            var valueInBase = ConvertViaFx(f.TotalBalance, f.CurrencyType, baseCcy);
            total += valueInBase;
            if (valueInBase > maxValue) maxValue = valueInBase;

            rows.Add(new CurrencyRow
            {
                Currency        = f.CurrencyType,
                CurrencyCode    = CurrencyHelper.GetIsoCode(f.CurrencyType),
                BalanceDisplay  = CurrencyHelper.Format(f.TotalBalance, f.CurrencyType),
                ReservedDisplay = f.ReservedBalance > 0m
                    ? "reserved " + CurrencyHelper.Format(f.ReservedBalance, f.CurrencyType)
                    : string.Empty,
                ValueInBase        = valueInBase,
                ValueInBaseDisplay = CurrencyHelper.Format(valueInBase, baseCcy),
            });
        }

        rows.Sort(static (a, b) => b.ValueInBase.CompareTo(a.ValueInBase));
        if (maxValue > 0m)
        {
            foreach (var r in rows) r.DepthRatio = (double)(r.ValueInBase / maxValue);
        }

        CurrentView = new ObservableCollection<CurrencyRow>(rows);
        TotalDisplay = "Total: " + CurrencyHelper.Format(total, baseCcy);
    }

    private decimal ConvertViaFx(decimal amount, CurrencyType from, CurrencyType to)
    {
        if (from == to) return CurrencyHelper.RoundMoney(amount, to);
        var mid = _fx.GetMidRate(from, to);
        return CurrencyHelper.RoundMoney(amount * mid, to);
    }
}

public sealed class CurrencyRow
{
    public required CurrencyType Currency { get; init; }
    public required string CurrencyCode { get; init; }
    public required string BalanceDisplay { get; init; }
    public required string ReservedDisplay { get; init; }
    public required decimal ValueInBase { get; init; }
    public required string ValueInBaseDisplay { get; init; }
    public double DepthRatio { get; set; }
    public bool HasReserved => !string.IsNullOrEmpty(ReservedDisplay);
}
