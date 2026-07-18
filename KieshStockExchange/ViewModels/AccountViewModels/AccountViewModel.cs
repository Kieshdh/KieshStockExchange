using System.Collections.ObjectModel;
using CommunityToolkit.Maui.Views;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KieshStockExchange.Helpers;
using KieshStockExchange.Models;
using KieshStockExchange.Services.BackgroundServices.Interfaces;
using KieshStockExchange.Services.DataServices.Interfaces;
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
    private readonly ITransactionService _transactions;
    private readonly IStockService _stocks;
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

    // Activity stats: read-only summary of the user's historical trading. Volume + realised P&L
    // are per-currency because currencies don't sum meaningfully. Realised P&L is computed
    // client-side via weighted-average cost basis per (StockId, CurrencyType). When any ledger
    // ever went negative (shorts opened before any matching long), PnLIsApproximate flips on.
    [ObservableProperty] private int _tradesPlaced;
    [ObservableProperty] private int _stocksTraded;
    [ObservableProperty] private bool _hasActivity;
    [ObservableProperty] private bool _hasVolume;
    [ObservableProperty] private bool _hasPnL;
    [ObservableProperty] private bool _pnLIsApproximate;
    [ObservableProperty] private string _bestStockDisplay = string.Empty;
    [ObservableProperty] private string _worstStockDisplay = string.Empty;
    [ObservableProperty] private bool _hasBestStock;
    [ObservableProperty] private bool _hasWorstStock;
    public ObservableCollection<AccountVolumeRow> VolumeByCurrency { get; } = new();
    public ObservableCollection<AccountPnLRow> PnLByCurrency { get; } = new();

    public IReadOnlyList<CurrencyType> AvailableCurrencies { get; } = CurrencyHelper.SupportedCurrencies;

    public AccountViewModel(
        IUserSessionService session,
        IUserPortfolioService portfolio,
        ITransactionService transactions,
        IStockService stocks,
        IAuthService auth,
        IProfileService profile,
        IServiceProvider services,
        TopNavBarViewModel topNavBarVm)
    {
        Title = "Account";
        _session      = session      ?? throw new ArgumentNullException(nameof(session));
        _portfolio    = portfolio    ?? throw new ArgumentNullException(nameof(portfolio));
        _transactions = transactions ?? throw new ArgumentNullException(nameof(transactions));
        _stocks       = stocks       ?? throw new ArgumentNullException(nameof(stocks));
        _auth         = auth         ?? throw new ArgumentNullException(nameof(auth));
        _profile      = profile      ?? throw new ArgumentNullException(nameof(profile));
        _services     = services     ?? throw new ArgumentNullException(nameof(services));
        TopNavBarVm   = topNavBarVm  ?? throw new ArgumentNullException(nameof(topNavBarVm));

        _session.SnapshotChanged          += OnSessionChanged;
        _portfolio.SnapshotChanged        += OnPortfolioChanged;
        _transactions.TransactionsChanged += OnTransactionsChanged;
        _stocks.CatalogChanged            += OnStocksChanged;

        RefreshAll();
        // Kick async remote refreshes so the Activity card reflects the latest tape on first nav
        // even if the user hasn't visited the trade-history or portfolio views this session.
        _ = KickBestEffort(_transactions.RefreshAsync(null), _stocks.EnsureLoadedAsync());
    }

    public void Refresh() => RefreshAll();

    // First-nav warm-up refreshes are best-effort — the cards also refresh via the service change
    // events — so a transient transport fault (cancel / premature-disconnect / IO, common when the
    // bot-busy server cuts a read) must not fault the unobserved-task net with scary noise. Swallow
    // those quietly; a genuinely unexpected exception still propagates so the global net logs it.
    private static async Task KickBestEffort(params Task[] tasks)
    {
        foreach (var t in tasks)
        {
            try { await t.ConfigureAwait(false); }
            catch (Exception ex) when (ex is OperationCanceledException
                                          or System.Net.Http.HttpRequestException
                                          or System.IO.IOException)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[AccountViewModel] best-effort refresh skipped (transient): {ex.Message}");
            }
        }
    }

    // Base-currency switch needs RefreshFunds too -- the funds card formats
    // against the session's base currency.
    private void OnSessionChanged(object? sender, SessionSnapshot e) =>
        MainThread.BeginInvokeOnMainThread(RefreshAll);

    private void OnPortfolioChanged(object? sender, EventArgs e) =>
        MainThread.BeginInvokeOnMainThread(RefreshFunds);

    private void OnTransactionsChanged(object? sender, EventArgs e) =>
        MainThread.BeginInvokeOnMainThread(RefreshActivity);

    // Stock catalog late-arrival: when the symbol lookup becomes available after first paint,
    // recompute so Best/Worst can swap from "#id" placeholders to real symbols.
    private void OnStocksChanged(object? sender, EventArgs e) =>
        MainThread.BeginInvokeOnMainThread(RefreshActivity);

    private void RefreshAll()
    {
        RefreshSession();
        RefreshFunds();
        RefreshActivity();
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

    private void RefreshActivity()
    {
        // AllTransactions is already user-scoped by TransactionService.RefreshAsync (server-side
        // filter by buyer/seller id), so we don't need to re-filter here.
        var txns = _transactions.AllTransactions;
        TradesPlaced = txns.Count;
        StocksTraded = txns.Select(t => t.StockId).Distinct().Count();

        VolumeByCurrency.Clear();
        foreach (var row in txns
            .GroupBy(t => t.CurrencyType)
            .Select(g => new AccountVolumeRow
            {
                CurrencyType = g.Key,
                Amount = g.Sum(t => t.TotalAmount),
            })
            .OrderByDescending(r => r.Amount))
        {
            VolumeByCurrency.Add(row);
        }
        HasVolume = VolumeByCurrency.Count > 0;

        RefreshPnL(txns);
        HasActivity = TradesPlaced > 0;
    }

    // Weighted-average cost basis per (stock, currency). Walks the tape oldest-first; buys blend
    // avg cost into the open lot, sells realise (sellPrice - avgCost) * qty. Naive on shorts:
    // if inventory ever goes negative, the running avg-cost loses its long-position meaning, so
    // the resulting P&L is best-effort. PnLIsApproximate flips on so the UI can warn.
    private void RefreshPnL(IReadOnlyList<Transaction> txns)
    {
        PnLByCurrency.Clear();
        BestStockDisplay = string.Empty;
        WorstStockDisplay = string.Empty;
        HasBestStock = HasWorstStock = false;
        PnLIsApproximate = false;

        var userId = _auth.CurrentUser?.UserId ?? 0;
        if (userId <= 0 || txns.Count == 0)
        {
            HasPnL = false;
            return;
        }

        var lots = new Dictionary<(int sid, CurrencyType ccy), (int qty, decimal avgCost)>();
        var pnlByCcy = new Dictionary<CurrencyType, decimal>();
        var pnlByStock = new Dictionary<(int sid, CurrencyType ccy), decimal>();
        bool anyShortLeg = false;

        // AllTransactions is newest-first; walk oldest-first so the cost basis evolves correctly.
        foreach (var t in txns.OrderBy(t => t.Timestamp))
        {
            var key = (t.StockId, t.CurrencyType);
            lots.TryGetValue(key, out var lot);
            bool isBuy = t.BuyerId == userId;

            if (isBuy)
            {
                // Crossing back through zero from a short rebases the avg cost to the new trade
                // price — the prior basis was a sell-side proceeds figure, not a buy cost.
                if (lot.qty <= 0)
                {
                    if (lot.qty < 0) anyShortLeg = true;
                    lots[key] = (lot.qty + t.Quantity, t.Price);
                }
                else
                {
                    var newQty = lot.qty + t.Quantity;
                    var newAvg = (lot.avgCost * lot.qty + t.Price * t.Quantity) / newQty;
                    lots[key] = (newQty, newAvg);
                }
            }
            else // sell
            {
                var realised = (t.Price - lot.avgCost) * t.Quantity;
                pnlByCcy.TryGetValue(t.CurrencyType, out var cExisting);
                pnlByCcy[t.CurrencyType] = cExisting + realised;
                pnlByStock.TryGetValue(key, out var sExisting);
                pnlByStock[key] = sExisting + realised;

                var newQty = lot.qty - t.Quantity;
                if (newQty < 0) anyShortLeg = true;
                lots[key] = (newQty, lot.avgCost);
            }
        }

        foreach (var (ccy, amt) in pnlByCcy.OrderByDescending(kv => kv.Value))
        {
            if (amt == 0m) continue;
            PnLByCurrency.Add(new AccountPnLRow { CurrencyType = ccy, Amount = amt });
        }
        HasPnL = PnLByCurrency.Count > 0;
        PnLIsApproximate = anyShortLeg && HasPnL;

        if (pnlByStock.Count > 0)
        {
            var best = pnlByStock.OrderByDescending(kv => kv.Value).First();
            var worst = pnlByStock.OrderBy(kv => kv.Value).First();
            if (best.Value > 0m)
            {
                BestStockDisplay = FormatStockPnL(best.Key.sid, best.Key.ccy, best.Value);
                HasBestStock = true;
            }
            if (worst.Value < 0m)
            {
                WorstStockDisplay = FormatStockPnL(worst.Key.sid, worst.Key.ccy, worst.Value);
                HasWorstStock = true;
            }
        }
    }

    // Symbol may not have arrived yet from StockService — fall back to "#id" so the row still
    // renders something useful. OnStocksChanged will trigger a recompute when the catalog loads.
    private string FormatStockPnL(int stockId, CurrencyType ccy, decimal amount)
    {
        var symbol = _stocks.ById.TryGetValue(stockId, out var s) && !string.IsNullOrEmpty(s.Symbol)
            ? s.Symbol
            : $"#{stockId}";
        return $"{symbol}  {CurrencyHelper.Format(amount, ccy)}";
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
        _session.SnapshotChanged          -= OnSessionChanged;
        _portfolio.SnapshotChanged        -= OnPortfolioChanged;
        _transactions.TransactionsChanged -= OnTransactionsChanged;
        _stocks.CatalogChanged            -= OnStocksChanged;
        TopNavBarVm.Dispose();
        _disposed = true;
        GC.SuppressFinalize(this);
    }
}
