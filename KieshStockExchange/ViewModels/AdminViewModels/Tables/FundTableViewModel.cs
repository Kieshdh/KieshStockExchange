using CommunityToolkit.Maui.Views;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KieshStockExchange.Helpers;
using KieshStockExchange.Models;
using KieshStockExchange.Services.DataServices.Interfaces;
using KieshStockExchange.Views.AdminPageViews.EditPopups;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace KieshStockExchange.ViewModels.AdminViewModels.Tables;

public partial class FundTableViewModel : BaseTableViewModel<FundTableObject>
{
    private const string AnyOption = "Any";

    public CurrencyType BaseCurrency = CurrencyType.USD;

    private readonly IServiceProvider _services;

    public IReadOnlyList<string> CurrencyFilterOptions { get; }

    [ObservableProperty] private string _userSearch = string.Empty;
    [ObservableProperty] private string _selectedCurrencyFilter = AnyOption;
    [ObservableProperty] private bool _hasNonZeroOnly;
    [ObservableProperty] private bool _hasReservedOnly;

    partial void OnUserSearchChanged(string value) => _ = ApplyViewChange();
    partial void OnSelectedCurrencyFilterChanged(string value) => _ = ApplyViewChange();
    partial void OnHasNonZeroOnlyChanged(bool value) => _ = ApplyViewChange();
    partial void OnHasReservedOnlyChanged(bool value) => _ = ApplyViewChange();

    public FundTableViewModel(IDataBaseService db, IServiceProvider services,
        ILogger<FundTableViewModel> logger) : base(db, logger)
    {
        Title = "Funds";
        SortKey = "UserId";
        SortDesc = false;
        _services = services ?? throw new ArgumentNullException(nameof(services));
        CurrencyFilterOptions = new[] { AnyOption }
            .Concat(CurrencyHelper.SupportedCurrencies.Select(c => c.ToString()))
            .ToList();
    }

    protected override async Task<(IReadOnlyList<FundTableObject> Items, int Total)> LoadPageAsync(
        int skip, int take, string? sortKey, bool desc, string? filter, CancellationToken ct)
    {
        int? userIdFilter = await ResolveUserIdAsync(ct);
        string? currencyArg = string.Equals(SelectedCurrencyFilter, AnyOption, StringComparison.Ordinal)
            ? null : SelectedCurrencyFilter;

        var (funds, total) = await _db.GetFundsPageAsync(skip, take, sortKey ?? "UserId", desc,
            userIdFilter, HasNonZeroOnly, HasReservedOnly, currencyArg, ct);

        if (funds.Count == 0) return (Array.Empty<FundTableObject>(), total);

        var userIds = funds.Select(f => f.UserId).Distinct().ToList();
        var users = await _db.GetUsersByIds(userIds, ct);
        var usersById = users.ToDictionary(u => u.UserId, u => u);

        // Per-user complete fund set for the converted-total column.
        var allFunds = await _db.GetFundsForUsersAsync(userIds, ct);
        var fundsByUser = allFunds.GroupBy(f => f.UserId)
            .ToDictionary(g => g.Key, g => g.ToList());

        var rows = funds.Select(f =>
        {
            usersById.TryGetValue(f.UserId, out var user);
            decimal converted = 0m;
            if (fundsByUser.TryGetValue(f.UserId, out var userFunds))
            {
                foreach (var uf in userFunds)
                    converted += CurrencyHelper.Convert(uf.TotalBalance, uf.CurrencyType, BaseCurrency);
            }
            return new FundTableObject(f, user?.Username ?? "Unknown",
                BaseCurrency, converted, OpenAdjustAsync);
        }).ToList();

        return (rows, total);
    }

    private async Task<int?> ResolveUserIdAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(UserSearch)) return null;
        var text = UserSearch.Trim();
        if (int.TryParse(text, out var id)) return id;
        var (matches, _) = await _db.GetUsersPageAsync(0, 1, "Username", false, text, ct);
        return matches.Count > 0 ? matches[0].UserId : -1;
    }

    private async Task OpenAdjustAsync(int userId, CurrencyType currency)
    {
        var page = Shell.Current?.CurrentPage
            ?? Application.Current?.Windows?.FirstOrDefault()?.Page;
        if (page is null) return;

        var popup = _services.GetRequiredService<FundAdjustPopup>();
        popup.ViewModel.Initialize(userId, currency);

        EventHandler? savedHandler = null;
        savedHandler = (_, _) => { _ = RefreshAsync(); };
        popup.ViewModel.Saved += savedHandler;
        try
        {
            await page.ShowPopupAsync(popup);
        }
        finally
        {
            popup.ViewModel.Saved -= savedHandler;
        }
    }
}

public partial class FundTableObject : ObservableObject
{
    public Fund Fund { get; }
    public string Username { get; }

    public int UserId => Fund.UserId;
    public string CurrencyDisplay => Fund.CurrencyType.ToString();
    public string TotalDisplay => Fund.TotalBalanceDisplay;
    public string ReservedDisplay => Fund.ReservedBalanceDisplay;
    public string AvailableDisplay => Fund.AvailableBalanceDisplay;
    public string UpdatedDisplay => Fund.UpdatedAtDisplay;
    public string TotalConvertedDisplay { get; }

    public IAsyncRelayCommand AdjustCommand { get; }

    public FundTableObject(Fund fund, string username, CurrencyType baseCurrency,
        decimal totalConverted, Func<int, CurrencyType, Task> onAdjust)
    {
        Fund = fund ?? throw new ArgumentNullException(nameof(fund));
        Username = username;
        TotalConvertedDisplay = CurrencyHelper.Format(totalConverted, baseCurrency);
        AdjustCommand = new AsyncRelayCommand(() => onAdjust(fund.UserId, fund.CurrencyType));
    }
}
