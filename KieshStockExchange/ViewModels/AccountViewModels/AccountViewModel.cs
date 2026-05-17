using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KieshStockExchange.Helpers;
using KieshStockExchange.Services.BackgroundServices.Interfaces;
using KieshStockExchange.Services.PortfolioServices.Interfaces;
using KieshStockExchange.Services.UserServices.Interfaces;
using KieshStockExchange.ViewModels.OtherViewModels;
using KieshStockExchange.Views.AccountPageViews;
using Microsoft.Extensions.DependencyInjection;
using System.Globalization;

namespace KieshStockExchange.ViewModels.AccountViewModels;

public partial class AccountViewModel : BaseViewModel, IDisposable
{
    private readonly IUserSessionService _session;
    private readonly IUserPortfolioService _portfolio;
    private readonly IAuthService _auth;
    private readonly IProfileService _profile;
    private readonly IServiceProvider _services;
    private bool _disposed;
    private bool _suppressCurrencyUpdate;

    public TopNavBarViewModel TopNavBarVm { get; }

    [ObservableProperty] private string _userName = string.Empty;
    [ObservableProperty] private string _fullName = string.Empty;
    [ObservableProperty] private string _email = string.Empty;
    [ObservableProperty] private string _birthDateDisplay = "—";
    [ObservableProperty] private string _memberSinceDisplay = "—";
    [ObservableProperty] private string _baseCurrency = string.Empty;
    [ObservableProperty] private string _fundsDisplay = "$ —";
    [ObservableProperty] private CurrencyType _selectedBaseCurrency;

    public IReadOnlyList<CurrencyType> AvailableCurrencies { get; } = CurrencyHelper.SupportedCurrencies;

    public AccountViewModel(
        IUserSessionService session,
        IUserPortfolioService portfolio,
        IAuthService auth,
        IProfileService profile,
        IServiceProvider services,
        TopNavBarViewModel topNavBarVm)
    {
        Title = "Account";
        _session    = session     ?? throw new ArgumentNullException(nameof(session));
        _portfolio  = portfolio   ?? throw new ArgumentNullException(nameof(portfolio));
        _auth       = auth        ?? throw new ArgumentNullException(nameof(auth));
        _profile    = profile     ?? throw new ArgumentNullException(nameof(profile));
        _services   = services    ?? throw new ArgumentNullException(nameof(services));
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
        BaseCurrency = snap.BaseCurrency.ToString();

        var user = _auth.CurrentUser;
        Email              = user?.Email ?? "—";
        BirthDateDisplay   = user?.BirthDateDisplay ?? "—";
        MemberSinceDisplay = user?.CreatedAtDisplay ?? "—";

        _suppressCurrencyUpdate = true;
        SelectedBaseCurrency = snap.BaseCurrency;
        _suppressCurrencyUpdate = false;
    }

    private void RefreshFunds()
    {
        var fund = _portfolio.GetBaseFund();
        FundsDisplay = fund == null
            ? "$ —"
            : $"$ {fund.AvailableBalance.ToString("N2", CultureInfo.InvariantCulture)}";
    }

    partial void OnSelectedBaseCurrencyChanged(CurrencyType value)
    {
        if (_suppressCurrencyUpdate) return;
        _ = _profile.UpdateBaseCurrencyAsync(value);
    }

    [RelayCommand]
    private async Task Logout()
    {
        // Confirm before tearing down the session — single mis-tap on a destructive action.
        var confirmed = await MainThread.InvokeOnMainThreadAsync(() =>
            Shell.Current.DisplayAlert("Log out",
                "Are you sure you want to log out?", "Log out", "Cancel"));
        if (!confirmed) return;

        await _auth.LogoutAsync().ConfigureAwait(false);
        _session.ClearSession();
        await MainThread.InvokeOnMainThreadAsync(() => Shell.Current.GoToAsync("///LoginPage"));
    }

    [RelayCommand] private void ChangeEmail()        => OpenInWindow<ChangeEmailPage>("Change Email");
    [RelayCommand] private void ChangePassword()     => OpenInWindow<ChangePasswordPage>("Change Password");
    [RelayCommand] private void ChangeUsername()     => OpenInWindow<ChangeUsernamePage>("Change Username");
    // The deposit/withdraw form has more rows (currency picker, balance, amount, note,
    // three buttons) so it needs more vertical room than the simple change-* forms.
    [RelayCommand] private void OpenDepositWithdraw() =>
        OpenInWindow<DepositWithdrawPage>("Deposit / Withdraw", width: 520, height: 700);
    // FX conversion form: two currency pickers + amount + preview, so a touch taller
    // than the deposit window.
    [RelayCommand] private void OpenConvertCurrency() =>
        OpenInWindow<ConvertCurrencyPage>("Convert currency", width: 520, height: 760);
    // Audit-trail companion: lists every Deposit/Withdraw the user has performed.
    [RelayCommand] private void OpenFundHistory() =>
        OpenInWindow<FundTransactionHistoryPage>("Fund history", width: 720, height: 600);

    private void OpenInWindow<TPage>(string title, double width = 480, double height = 520)
        where TPage : ContentPage
    {
        var page = _services.GetRequiredService<TPage>();
        var window = new Window(page)
        {
            Title  = title,
            Width  = width,
            Height = height
        };
        window.Destroying += (_, __) => MainThread.BeginInvokeOnMainThread(RefreshAll);
        Application.Current?.OpenWindow(window);
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
