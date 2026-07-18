using CommunityToolkit.Maui.Views;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KieshStockExchange.Helpers;
using KieshStockExchange.Models;
using KieshStockExchange.Services.DataServices.Interfaces;
using KieshStockExchange.Services.MarketDataServices.Interfaces;
using KieshStockExchange.ViewModels.OtherViewModels;
using KieshStockExchange.Views.AdminPageViews.EditPopups;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Collections.ObjectModel;

namespace KieshStockExchange.ViewModels.AdminViewModels.Tables;

public partial class UserDetailsViewModel : BaseViewModel, ILazyTab
{
    private const int ActivityWindow = 25;

    public event EventHandler<int>? OrderSelected;
    public event EventHandler<int>? TransactionSelected;

    private readonly IDataBaseService _db;
    private readonly IMarketDataService _market;
    private readonly IServiceProvider _services;
    private readonly ILogger<UserDetailsViewModel> _logger;

    private User? _loadedUser;
    private Dictionary<int, Stock> _stocksById = new();

    [ObservableProperty] private string _userSearch = string.Empty;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUser))]
    [NotifyPropertyChangedFor(nameof(HasStatus))]
    private bool _hasLoadedUser;

    public bool HasUser => HasLoadedUser;

    private List<User> _allUsers = new();
    public ObservableCollection<User> Suggestions { get; } = new();
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatus))]
    private string _statusMessage = "No user selected";
    public bool HasSuggestions => Suggestions.Count > 0;
    public bool HasStatus => !string.IsNullOrEmpty(StatusMessage);

    partial void OnUserSearchChanged(string value)
    {
        UpdateSuggestions();
        UpdateStatusMessage();
    }

    private void UpdateSuggestions()
    {
        Suggestions.Clear();
        if (_allUsers.Count == 0 || string.IsNullOrWhiteSpace(UserSearch))
        {
            OnPropertyChanged(nameof(HasSuggestions));
            return;
        }
        var text = UserSearch.Trim();
        var matches = _allUsers
            .Where(u => u.Username.Contains(text, StringComparison.OrdinalIgnoreCase)
                     || u.UserId.ToString() == text)
            .Take(20);
        foreach (var u in matches) Suggestions.Add(u);
        OnPropertyChanged(nameof(HasSuggestions));
    }

    private void UpdateStatusMessage()
    {
        if (HasUser) { StatusMessage = string.Empty; return; }
        if (string.IsNullOrWhiteSpace(UserSearch)) { StatusMessage = "No user selected"; return; }
        StatusMessage = Suggestions.Count == 0 ? "User not found" : string.Empty;
    }

    [RelayCommand]
    private async Task PickUser(User? user)
    {
        if (user is null) return;
        UserSearch = string.Empty;
        Suggestions.Clear();
        OnPropertyChanged(nameof(HasSuggestions));
        await LoadUserByIdAsync(user.UserId);
        UpdateStatusMessage();
    }

    #region Identity card (inline-editable)
    [ObservableProperty] private int _userId;
    [ObservableProperty] private string _username = string.Empty;
    [ObservableProperty] private string _fullName = string.Empty;
    [ObservableProperty] private string _email = string.Empty;
    [ObservableProperty] private string _birthDateText = string.Empty;
    [ObservableProperty] private bool _isAdmin;
    [ObservableProperty] private string _memberSinceDisplay = string.Empty;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasIdentityError))]
    private string _identityErrorMessage = string.Empty;
    [ObservableProperty] private string _identityStatusMessage = string.Empty;
    public bool HasIdentityError => !string.IsNullOrEmpty(IdentityErrorMessage);
    #endregion

    public ObservableCollection<UserDetailsFundRow> Funds { get; } = new();
    public ObservableCollection<UserDetailsPositionRow> Positions { get; } = new();
    public ObservableCollection<UserDetailsOrderRow> RecentOrders { get; } = new();
    public ObservableCollection<UserDetailsTransactionRow> RecentTransactions { get; } = new();

    public UserDetailsViewModel(IDataBaseService db, IMarketDataService market,
        IServiceProvider services, ILogger<UserDetailsViewModel> logger)
    {
        Title = "User";
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _market = market ?? throw new ArgumentNullException(nameof(market));
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task EnsureInitializedAsync()
    {
        if (_allUsers.Count > 0) return;
        try
        {
            // Cache the full user list so autocomplete matches locally per keystroke.
            var (users, _) = await _db.GetUsersPageAsync(0, int.MaxValue, "Username", false, null);
            _allUsers = users.OrderBy(u => u.Username, StringComparer.OrdinalIgnoreCase).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "UserDetails: failed to load all-users list for autocomplete.");
        }
    }

    public async Task RefreshAsync()
    {
        if (_loadedUser is not null) await LoadUserByIdAsync(_loadedUser.UserId);
    }

    [RelayCommand]
    private Task Refresh() => RefreshAsync();

    [RelayCommand]
    private async Task LoadUserAsync()
    {
        IdentityErrorMessage = string.Empty;
        IdentityStatusMessage = string.Empty;

        if (string.IsNullOrWhiteSpace(UserSearch))
        {
            IdentityErrorMessage = "Type a username or user id.";
            return;
        }

        User? user = null;
        var text = UserSearch.Trim();
        if (int.TryParse(text, out var id))
            user = await _db.GetUserById(id);
        else
        {
            var (matches, _) = await _db.GetUsersPageAsync(0, 1, "Username", false, text);
            user = matches.FirstOrDefault();
        }

        if (user is null)
        {
            ClearUserState();
            IdentityErrorMessage = $"No user matches '{text}'.";
            return;
        }

        await LoadUserByIdAsync(user.UserId);
    }

    public async Task LoadUserByIdAsync(int userId)
    {
        IsBusy = true;
        try
        {
            var user = await _db.GetUserById(userId);
            if (user is null) { ClearUserState(); return; }

            _loadedUser = user;
            ApplyIdentityFromUser(user);

            if (_stocksById.Count == 0)
            {
                var stocks = await _db.GetStocksAsync();
                _stocksById = stocks.ToDictionary(s => s.StockId);
            }

            await Task.WhenAll(LoadFundsAsync(user.UserId),
                               LoadPositionsAsync(user.UserId),
                               LoadActivityAsync(user.UserId));

            HasLoadedUser = true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "UserDetails: load failed for user #{UserId}", userId);
            IdentityErrorMessage = "Failed to load user.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void ApplyIdentityFromUser(User user)
    {
        UserId = user.UserId;
        Username = user.Username;
        FullName = user.FullName;
        Email = user.Email;
        BirthDateText = user.BirthDate?.ToLocalTime().ToString("dd/MM/yyyy") ?? string.Empty;
        IsAdmin = user.IsAdmin;
        MemberSinceDisplay = user.CreatedAtDisplay;
    }

    private void ClearUserState()
    {
        _loadedUser = null;
        HasLoadedUser = false;
        UserId = 0;
        Username = FullName = Email = BirthDateText = MemberSinceDisplay = string.Empty;
        IsAdmin = false;
        Funds.Clear();
        Positions.Clear();
        RecentOrders.Clear();
        RecentTransactions.Clear();
    }

    [RelayCommand]
    private async Task SaveIdentityAsync()
    {
        if (_loadedUser is null) return;
        IdentityErrorMessage = string.Empty;
        IdentityStatusMessage = string.Empty;

        if (string.IsNullOrWhiteSpace(Username) || string.IsNullOrWhiteSpace(FullName)
            || string.IsNullOrWhiteSpace(Email) || string.IsNullOrWhiteSpace(BirthDateText))
        {
            IdentityErrorMessage = "All identity fields are required.";
            return;
        }

        if (!DateTime.TryParseExact(BirthDateText, "dd/MM/yyyy", null,
                System.Globalization.DateTimeStyles.None, out var parsedDate))
        {
            IdentityErrorMessage = "Birthdate must be dd/MM/yyyy.";
            return;
        }

        var draft = new User
        {
            UserId = _loadedUser.UserId,
            PasswordHash = _loadedUser.PasswordHash,
            CreatedAt = _loadedUser.CreatedAt,
            Username = Username.Trim(),
            FullName = FullName.Trim(),
            Email = Email.Trim(),
            BirthDate = TimeHelper.EnsureUtc(parsedDate),
            IsAdmin = IsAdmin,
        };

        if (!draft.IsValidUsername())  { IdentityErrorMessage = "Username must be alphanumeric and 5–20 characters."; return; }
        if (!draft.IsValidName())      { IdentityErrorMessage = "Full name is invalid (3–100 chars)."; return; }
        if (!draft.IsValidEmail())     { IdentityErrorMessage = "Email format is invalid."; return; }
        if (!draft.IsValidBirthdate()) { IdentityErrorMessage = "User must be at least 18 years old."; return; }

        if (!string.Equals(draft.Username, _loadedUser.Username, StringComparison.OrdinalIgnoreCase))
        {
            var existing = await _db.GetUserByUsername(draft.Username);
            if (existing is not null && existing.UserId != draft.UserId)
            {
                IdentityErrorMessage = $"Username '{draft.Username}' is already taken.";
                return;
            }
        }

        IsBusy = true;
        try
        {
            await _db.UpsertUser(draft);
            _loadedUser = draft;
            IdentityStatusMessage = "Saved.";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "UserDetails: identity save failed for user #{UserId}", draft.UserId);
            IdentityErrorMessage = "Save failed.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    #region Funds card
    private async Task LoadFundsAsync(int userId)
    {
        var funds = await _db.GetFundsByUserId(userId);
        var byCurrency = funds.ToDictionary(f => f.CurrencyType, f => f);
        Funds.Clear();
        foreach (var currency in CurrencyHelper.SupportedCurrencies)
        {
            byCurrency.TryGetValue(currency, out var fund);
            fund ??= new Fund { UserId = userId, CurrencyType = currency };
            Funds.Add(new UserDetailsFundRow(fund, () => OpenAdjustAsync(userId, currency)));
        }
    }

    private async Task OpenAdjustAsync(int userId, CurrencyType currency)
    {
        var page = Shell.Current?.CurrentPage
            ?? Application.Current?.Windows?.FirstOrDefault()?.Page;
        if (page is null) return;

        var popup = _services.GetRequiredService<FundAdjustPopup>();
        popup.ViewModel.Initialize(userId, currency);

        EventHandler? savedHandler = null;
        savedHandler = (_, _) => { _ = LoadFundsAsync(userId); };
        popup.ViewModel.Saved += savedHandler;
        try { await page.ShowPopupAsync(popup); }
        finally { popup.ViewModel.Saved -= savedHandler; }
    }
    #endregion

    #region Positions card
    private async Task LoadPositionsAsync(int userId)
    {
        var positions = await _db.GetPositionsByUserId(userId);
        Positions.Clear();
        foreach (var pos in positions.OrderBy(p => p.StockId))
        {
            var symbol = _stocksById.TryGetValue(pos.StockId, out var s) ? s.Symbol : $"#{pos.StockId}";
            decimal? price = null;
            if (_stocksById.TryGetValue(pos.StockId, out var stock))
            {
                try { price = await _market.GetLastPriceAsync(pos.StockId, CurrencyType.USD); }
                catch { price = null; }
            }
            Positions.Add(new UserDetailsPositionRow(pos, symbol, price, () => OpenPositionEditAsync(userId, pos.StockId)));
        }
    }

    private async Task OpenPositionEditAsync(int userId, int stockId)
    {
        var page = Shell.Current?.CurrentPage
            ?? Application.Current?.Windows?.FirstOrDefault()?.Page;
        if (page is null) return;

        var positions = await _db.GetPositionsByUserId(userId);
        var position = positions.FirstOrDefault(p => p.StockId == stockId);
        if (position is null) return;

        var symbol = _stocksById.TryGetValue(stockId, out var s) ? s.Symbol : $"#{stockId}";
        var popup = _services.GetRequiredService<PositionEditPopup>();
        popup.ViewModel.Initialize(position, symbol);

        EventHandler? savedHandler = null;
        savedHandler = (_, _) => { _ = LoadPositionsAsync(userId); };
        popup.ViewModel.Saved += savedHandler;
        try { await page.ShowPopupAsync(popup); }
        finally { popup.ViewModel.Saved -= savedHandler; }
    }
    #endregion

    #region Activity card
    private async Task LoadActivityAsync(int userId)
    {
        var (orders, _) = await _db.GetOrdersPageAsync(0, ActivityWindow, "CreatedAt", desc: true,
            new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            DateTime.UtcNow.AddDays(1),
            statusFilter: null,
            userIdFilter: userId);

        var (transactions, _) = await _db.GetTransactionsPageAsync(0, ActivityWindow, "Timestamp", desc: true,
            new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            DateTime.UtcNow.AddDays(1),
            userIdFilter: userId);

        RecentOrders.Clear();
        foreach (var o in orders)
        {
            var symbol = _stocksById.TryGetValue(o.StockId, out var s) ? s.Symbol : $"#{o.StockId}";
            RecentOrders.Add(new UserDetailsOrderRow(o, symbol, oid => OrderSelected?.Invoke(this, oid)));
        }

        RecentTransactions.Clear();
        foreach (var t in transactions)
        {
            var symbol = _stocksById.TryGetValue(t.StockId, out var s) ? s.Symbol : $"#{t.StockId}";
            RecentTransactions.Add(new UserDetailsTransactionRow(t, symbol, userId,
                tid => TransactionSelected?.Invoke(this, tid)));
        }
    }
    #endregion
}
