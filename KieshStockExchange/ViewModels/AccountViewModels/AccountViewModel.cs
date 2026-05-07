using CommunityToolkit.Mvvm.ComponentModel;
using KieshStockExchange.Services.BackgroundServices.Interfaces;
using KieshStockExchange.Services.PortfolioServices.Interfaces;
using KieshStockExchange.ViewModels.OtherViewModels;
using System.Globalization;

namespace KieshStockExchange.ViewModels.AccountViewModels;

public partial class AccountViewModel : BaseViewModel, IDisposable
{
    private readonly IUserSessionService _session;
    private readonly IUserPortfolioService _portfolio;
    private bool _disposed;

    public TopNavBarViewModel TopNavBarVm { get; }

    [ObservableProperty] private string _userName = string.Empty;
    [ObservableProperty] private string _fullName = string.Empty;
    [ObservableProperty] private string _userId = string.Empty;
    [ObservableProperty] private string _baseCurrency = string.Empty;
    [ObservableProperty] private string _fundsDisplay = "$ —";

    public AccountViewModel(IUserSessionService session, IUserPortfolioService portfolio,
        TopNavBarViewModel topNavBarVm)
    {
        Title = "Account";
        _session    = session     ?? throw new ArgumentNullException(nameof(session));
        _portfolio  = portfolio   ?? throw new ArgumentNullException(nameof(portfolio));
        TopNavBarVm = topNavBarVm ?? throw new ArgumentNullException(nameof(topNavBarVm));

        _session.SnapshotChanged   += OnSessionChanged;
        _portfolio.SnapshotChanged += OnPortfolioChanged;

        RefreshAll();
    }

    public void Refresh() => RefreshAll();

    private void OnSessionChanged(object? sender, SessionSnapshot e) =>
        MainThread.BeginInvokeOnMainThread(RefreshSession);

    private void OnPortfolioChanged(object? sender, EventArgs e) =>
        MainThread.BeginInvokeOnMainThread(RefreshFunds);

    private void RefreshAll()
    {
        RefreshSession();
        RefreshFunds();
    }

    private void RefreshSession()
    {
        var snap = _session.Snapshot;
        UserName     = snap.UserName;
        FullName     = snap.FullName;
        UserId       = snap.UserId > 0 ? snap.UserId.ToString() : "—";
        BaseCurrency = snap.BaseCurrency.ToString();
    }

    private void RefreshFunds()
    {
        var fund = _portfolio.GetBaseFund();
        FundsDisplay = fund == null
            ? "$ —"
            : $"$ {fund.AvailableBalance.ToString("N2", CultureInfo.InvariantCulture)}";
    }

    public void Dispose()
    {
        if (_disposed) return;
        _session.SnapshotChanged   -= OnSessionChanged;
        _portfolio.SnapshotChanged -= OnPortfolioChanged;
        TopNavBarVm.Dispose();
        _disposed = true;
        GC.SuppressFinalize(this);
    }
}
