using CommunityToolkit.Maui.Views;
using CommunityToolkit.Mvvm.ComponentModel;
using KieshStockExchange.Helpers;
using KieshStockExchange.Models;
using KieshStockExchange.Services.DataServices.Interfaces;
using KieshStockExchange.Views.AdminPageViews.EditPopups;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace KieshStockExchange.ViewModels.AdminViewModels.Tables;

public partial class TransactionTableViewModel : DateRangeTableViewModel<TransactionTableObject>
{
    private const string DefaultSortKey = "Timestamp";

    protected override Stock AnyStock => OrderTableViewModel.AnyStockSentinel;

    #region Filter state
    [ObservableProperty] private string _selectedCurrencyFilter = AnyOption;

    public IReadOnlyList<string> CurrencyFilterOptions { get; }

    partial void OnSelectedCurrencyFilterChanged(string value) => _ = ApplyViewChange();
    #endregion

    #region Fields, events and Constructor
    private readonly IServiceProvider _services;

    public event EventHandler<int>? UserSelected;
    public event EventHandler<int>? OrderSelected;

    public TransactionTableViewModel(IDataBaseService dbService, IServiceProvider services,
        ILogger<TransactionTableViewModel> logger) : base(dbService, logger)
    {
        Title = "Transactions";
        SortKey = DefaultSortKey;
        SortDesc = true;
        _services = services ?? throw new ArgumentNullException(nameof(services));
        CurrencyFilterOptions = new[] { AnyOption }
            .Concat(CurrencyHelper.SupportedCurrencies.Select(c => c.ToString()))
            .ToList();
    }

    internal void RaiseUserSelected(int userId) => UserSelected?.Invoke(this, userId);
    internal void RaiseOrderSelected(int orderId) => OrderSelected?.Invoke(this, orderId);
    #endregion

    #region Page loading
    protected override async Task<(IReadOnlyList<TransactionTableObject> Items, int Total)> LoadPageAsync(
        int skip, int take, string? sortKey, bool desc, string? filter, CancellationToken ct)
    {
        await EnsureStocksByIdAsync(ct);

        int? userIdFilter = await ResolveUserIdFilterAsync(ct);
        int? stockIdFilter = SelectedStockFilter is { StockId: > 0 } s ? s.StockId : null;
        string? currencyArg = string.Equals(SelectedCurrencyFilter, AnyOption, StringComparison.Ordinal) ? null : SelectedCurrencyFilter;
        IList<int>? excludeIds = HideAiBots ? _aiUserIds : null;

        // Combine date+time pickers; clamp upper bound to now.
        var (fromCombined, toCombined) = DateRangeHelper.CombineAndClampRange(FromDate, FromTime, ToDate, ToTime);

        var (transactions, total) = await _db.GetTransactionsPageAsync(skip, take, sortKey ?? DefaultSortKey, desc,
            fromCombined, toCombined,
            userIdFilter, stockIdFilter, currencyArg, excludeIds, ct);

        if (transactions.Count == 0) return (Array.Empty<TransactionTableObject>(), total);

        var userIds = transactions.SelectMany(t => new[] { t.BuyerId, t.SellerId }).Distinct().ToList();
        var users = await _db.GetUsersByIds(userIds, ct);
        var usersById = users.ToDictionary(u => u.UserId, u => u);

        var rows = transactions.Select(t =>
        {
            if (!usersById.TryGetValue(t.BuyerId, out var buyer))
                buyer = new User { UserId = t.BuyerId, Username = "Unknown" };
            if (!usersById.TryGetValue(t.SellerId, out var seller))
                seller = new User { UserId = t.SellerId, Username = "Unknown" };
            if (!_stocksById.TryGetValue(t.StockId, out var stock))
                stock = new Stock { StockId = t.StockId, CompanyName = "Unknown", Symbol = "-" };
            return new TransactionTableObject(t, buyer, seller, stock, OpenDetailsAsync);
        }).ToList();

        // In-VM sort for Total / BuyerName / SellerName (computed or post-join).
        IEnumerable<TransactionTableObject> ordered = (sortKey, desc) switch
        {
            ("Total",      true)  => rows.OrderByDescending(r => r.Transaction.Price * r.Transaction.Quantity),
            ("Total",      false) => rows.OrderBy(r => r.Transaction.Price * r.Transaction.Quantity),
            ("BuyerName",  true)  => rows.OrderByDescending(r => r.Buyer.Username, StringComparer.OrdinalIgnoreCase),
            ("BuyerName",  false) => rows.OrderBy(r => r.Buyer.Username, StringComparer.OrdinalIgnoreCase),
            ("SellerName", true)  => rows.OrderByDescending(r => r.Seller.Username, StringComparer.OrdinalIgnoreCase),
            ("SellerName", false) => rows.OrderBy(r => r.Seller.Username, StringComparer.OrdinalIgnoreCase),
            _ => rows
        };
        return (ordered.ToList(), total);
    }
    #endregion

    #region Details popup
    private async Task OpenDetailsAsync(Transaction tx, User buyer, User seller, Stock stock)
    {
        var page = Shell.Current?.CurrentPage
            ?? Application.Current?.Windows?.FirstOrDefault()?.Page;
        if (page is null) return;

        var popup = _services.GetRequiredService<TransactionDetailsPopup>();
        popup.ViewModel.Initialize(tx, buyer.Username, seller.Username, stock.Symbol);

        EventHandler<int>? userNav = (_, uid) => RaiseUserSelected(uid);
        EventHandler<int>? orderNav = (_, oid) => RaiseOrderSelected(oid);
        popup.ViewModel.NavigateToUserRequested += userNav;
        popup.ViewModel.NavigateToOrderRequested += orderNav;
        try { await page.ShowPopupAsync(popup); }
        finally
        {
            popup.ViewModel.NavigateToUserRequested -= userNav;
            popup.ViewModel.NavigateToOrderRequested -= orderNav;
        }
    }
    #endregion
}
