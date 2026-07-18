using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KieshStockExchange.Models;
using KieshStockExchange.Services.DataServices.Interfaces;
using KieshStockExchange.Views.AdminPageViews.EditPopups;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using CommunityToolkit.Maui.Views;

namespace KieshStockExchange.ViewModels.AdminViewModels.Tables;

public partial class UserTableViewModel : BaseTableViewModel<UserTableObject>
{
    #region Fields, events and Constructor
    private const string DefaultSortKey = "CreatedAt";
    private readonly IServiceProvider _services;

    /// <summary> Raised when a row's View is tapped so AdminViewModel can switch to the User-details tab. </summary>
    public event EventHandler<int>? UserSelected;

    [ObservableProperty] private string _searchText = string.Empty;

    partial void OnSearchTextChanged(string value)
    {
        CurrentFilter = string.IsNullOrWhiteSpace(value) ? null : value;
        _ = ApplyViewChange();
    }

    public UserTableViewModel(IDataBaseService dbService, IServiceProvider services,
        ILogger<UserTableViewModel> logger)
        : base(dbService, logger)
    {
        Title = "Users";
        SortKey = DefaultSortKey;
        SortDesc = true;
        _services = services ?? throw new ArgumentNullException(nameof(services));
    }
    #endregion

    #region Page loading and edit popup
    protected override async Task<(IReadOnlyList<UserTableObject> Items, int Total)> LoadPageAsync(
        int skip, int take, string? sortKey, bool desc, string? filter, CancellationToken ct)
    {
        var (users, total) = await _db.GetUsersPageAsync(skip, take, sortKey ?? DefaultSortKey, desc, filter, ct);
        var rows = users.Select(u => new UserTableObject(u, OpenEditAsync, RaiseUserSelected)).ToList();
        return (rows, total);
    }

    private void RaiseUserSelected(int userId) => UserSelected?.Invoke(this, userId);

    private async Task OpenEditAsync(User user)
    {
        var page = Shell.Current?.CurrentPage
            ?? Application.Current?.Windows?.FirstOrDefault()?.Page;
        if (page is null) return;

        var popup = _services.GetRequiredService<UserEditPopup>();
        popup.ViewModel.Initialize(user);

        EventHandler? savedHandler = (_, _) => { _ = RefreshAsync(); };
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
