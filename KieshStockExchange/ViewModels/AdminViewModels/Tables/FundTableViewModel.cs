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

    private const string DefaultSortKey = "UserId";

    public CurrencyType BaseCurrency = CurrencyType.USD;

    #region Fields, filter state and Constructor
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
        SortKey = DefaultSortKey;
        SortDesc = false;
        _services = services ?? throw new ArgumentNullException(nameof(services));
        CurrencyFilterOptions = new[] { AnyOption }
            .Concat(CurrencyHelper.SupportedCurrencies.Select(c => c.ToString()))
            .ToList();
    }
    #endregion

    #region Page loading and adjust popup
    protected override async Task<(IReadOnlyList<FundTableObject> Items, int Total)> LoadPageAsync(
        int skip, int take, string? sortKey, bool desc, string? filter, CancellationToken ct)
    {
        int? userIdFilter = await ResolveUserIdAsync(ct);
        string? currencyArg = string.Equals(SelectedCurrencyFilter, AnyOption, StringComparison.Ordinal)
            ? null : SelectedCurrencyFilter;

        var (funds, total) = await _db.GetFundsPageAsync(skip, take, sortKey ?? DefaultSortKey, desc,
            userIdFilter, HasNonZeroOnly, HasReservedOnly, currencyArg, ct);

        if (funds.Count == 0) return (Array.Empty<FundTableObject>(), total);

        var userIds = funds.Select(f => f.UserId).Distinct().ToList();
        var users = await _db.GetUsersByIds(userIds, ct);
        var usersById = users.ToDictionary(u => u.UserId, u => u);

        // Fetch every fund per user so the converted-total column sums all currencies.
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
    #endregion
}
