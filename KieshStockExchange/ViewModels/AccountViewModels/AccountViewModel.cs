using System.Collections.ObjectModel;
using CommunityToolkit.Maui.Views;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KieshStockExchange.Helpers;
using KieshStockExchange.Models;
using KieshStockExchange.Services.BackgroundServices.Interfaces;
using KieshStockExchange.Services.PortfolioServices.Interfaces;
using KieshStockExchange.Services.UserServices.Interfaces;
// IWatchlistService is resolved at logout (and only there) via _services, so we
// don't add another constructor dependency just for the teardown call.
using KieshStockExchange.ViewModels.OtherViewModels;
using KieshStockExchange.Views.AccountPageViews;
using Microsoft.Extensions.DependencyInjection;

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
    [ObservableProperty] private string _reservedDisplay = string.Empty;
    [ObservableProperty] private bool _hasReserved;
    [ObservableProperty] private CurrencyType _selectedBaseCurrency;

    // Sibling currencies: every fund the user holds OTHER than the session base. Empty when the
    // user only operates in one currency. Surfaces multi-currency holdings here so the user
    // doesn't have to go to the Portfolio page just to verify a non-base balance is there.
    public ObservableCollection<AccountFundRow> OtherCurrencyFunds { get; } = new();
    [ObservableProperty] private bool _hasOtherCurrencyFunds;

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

    // Base-currency switch needs RefreshFunds too -- the funds card formats
    // against the session's base currency.
    private void OnSessionChanged(object? sender, SessionSnapshot e) =>
        MainThread.BeginInvokeOnMainThread(RefreshAll);

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
        // Look up the fund for the session's current base currency directly --
        // _portfolio.GetBaseFund() reads a stale internal copy that's only
        // updated on portfolio refresh, not on session BaseCurrency changes.
        var baseCcy = _session.BaseCurrency;
        var baseFund = _portfolio.GetFundByCurrency(baseCcy);
        FundsDisplay = CurrencyHelper.Format(baseFund?.AvailableBalance ?? 0m, baseCcy);
        // Reserved is shown only when > 0 — most users won't have any pending reservation, and
        // an "Reserved: $0" line would just be noise.
        var reserved = baseFund?.ReservedBalance ?? 0m;
        HasReserved = reserved > 0m;
        ReservedDisplay = HasReserved
            ? $"Reserved {CurrencyHelper.Format(reserved, baseCcy)}"
            : string.Empty;

        // Sibling-currency rows: every other fund the user holds with a non-zero balance, sorted
        // by available balance descending. Replaces the prior "you can only see base here" gap.
        OtherCurrencyFunds.Clear();
        foreach (var f in _portfolio.GetFunds()
            .Where(f => f.CurrencyType != baseCcy && f.TotalBalance > 0m)
            .OrderByDescending(f => f.AvailableBalance))
        {
            OtherCurrencyFunds.Add(new AccountFundRow { Fund = f });
        }
        HasOtherCurrencyFunds = OtherCurrencyFunds.Count > 0;
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
        _services.GetService<IWatchlistService>()?.Clear();
        await MainThread.InvokeOnMainThreadAsync(() => Shell.Current.GoToAsync("///LoginPage"));
    }

    [RelayCommand] private Task ChangeEmail()         => ShowAccountPopupAsync<ChangeEmailPage>();
    [RelayCommand] private Task ChangePassword()      => ShowAccountPopupAsync<ChangePasswordPage>();
    [RelayCommand] private Task ChangeUsername()      => ShowAccountPopupAsync<ChangeUsernamePage>();
    [RelayCommand] private Task OpenDepositWithdraw() => ShowAccountPopupAsync<DepositWithdrawPage>();
    [RelayCommand] private Task OpenConvertCurrency() => ShowAccountPopupAsync<ConvertCurrencyPage>();
    [RelayCommand] private Task OpenFundHistory()     => ShowAccountPopupAsync<FundTransactionHistoryPage>();

    private async Task ShowAccountPopupAsync<TPopup>() where TPopup : Popup
    {
        var popup = _services.GetRequiredService<TPopup>();
        var page = Shell.Current?.CurrentPage
            ?? Application.Current?.Windows?.FirstOrDefault()?.Page;
        if (page is null) return;

        // ShowPopupAsync awaits until the popup is dismissed — same close-then-refresh
        // contract the old Window.Destroying handler provided.
        await page.ShowPopupAsync(popup);
        RefreshAll();
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

// Row for the "other currencies" sub-list on the Funds card. Wraps a Fund and projects the
// display strings the row binds to — keeps the XAML free of nested .Fund.* paths and gives the
// page a stable shape even if Fund grows new properties.
public sealed class AccountFundRow
{
    public required Fund Fund { get; init; }
    public string Currency => Fund.CurrencyType.ToString();
    public string AvailableDisplay => Fund.AvailableBalanceDisplay;
    public string ReservedDisplay => Fund.ReservedBalanceDisplay;
    public bool HasReserved => Fund.ReservedBalance > 0m;
}
