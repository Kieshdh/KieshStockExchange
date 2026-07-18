using CommunityToolkit.Mvvm.ComponentModel;
using KieshStockExchange.Models;
using KieshStockExchange.Services.DataServices.Interfaces;
using Microsoft.Extensions.Logging;

namespace KieshStockExchange.ViewModels.AdminViewModels.Tables;

public partial class FundTransactionTableViewModel : BaseTableViewModel<FundTransactionTableObject>
{
    #region Fields and Constructor
    [ObservableProperty] private string _searchText = string.Empty;

    partial void OnSearchTextChanged(string value) => _ = ApplyViewChange();

    public FundTransactionTableViewModel(IDataBaseService dbService,
        ILogger<FundTransactionTableViewModel> logger) : base(dbService, logger)
    {
        Title = "Fund Tx";
        SortKey = "CreatedAt";
        SortDesc = true;
    }
    #endregion

    #region Page loading
    // Read-only: fund transactions are the deliberate audit log of cash movements, so
    // there is no edit/adjust affordance (unlike Funds). Conversions appear as two rows
    // (ConversionIn + ConversionOut), matching how they are stored.
    protected override async Task<(IReadOnlyList<FundTransactionTableObject> Items, int Total)> LoadPageAsync(
        int skip, int take, string? sortKey, bool desc, string? filter, CancellationToken ct)
    {
        int? userIdFilter = await ResolveUserIdAsync(ct);

        var (txs, total) = await _db.GetFundTransactionsPageAsync(
            skip, take, sortKey ?? "CreatedAt", desc, userIdFilter, ct);

        if (txs.Count == 0) return (Array.Empty<FundTransactionTableObject>(), total);

        var userIds = txs.Select(t => t.UserId).Distinct().ToList();
        var users = await _db.GetUsersByIds(userIds, ct);
        var usersById = users.ToDictionary(u => u.UserId, u => u);

        var rows = txs.Select(t =>
        {
            usersById.TryGetValue(t.UserId, out var user);
            return new FundTransactionTableObject(t, user?.Username ?? "Unknown");
        }).ToList();
        return (rows, total);
    }

    // Numeric search → exact UserId; text → resolve to the first matching user, or -1
    // (no match) so the page comes back empty rather than unfiltered.
    private async Task<int?> ResolveUserIdAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(SearchText)) return null;
        var text = SearchText.Trim();
        if (int.TryParse(text, out var id)) return id;
        var (matches, _) = await _db.GetUsersPageAsync(0, 1, "Username", false, text, ct);
        return matches.Count > 0 ? matches[0].UserId : -1;
    }
    #endregion
}
