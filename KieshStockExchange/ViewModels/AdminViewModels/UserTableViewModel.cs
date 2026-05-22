using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KieshStockExchange.Models;
using KieshStockExchange.Services.DataServices.Interfaces;
using KieshStockExchange.Views.AdminPageViews.EditPopups;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using CommunityToolkit.Maui.Views;

namespace KieshStockExchange.ViewModels.AdminViewModels;

public partial class UserTableViewModel : BaseTableViewModel<UserTableObject>
{
    private readonly IServiceProvider _services;

    // Raised when an admin taps the per-row 'View' label so the AdminViewModel
    // can switch to the User-details tab and pre-load the picked user.
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
        SortKey = "CreatedAt";
        SortDesc = true;
        _services = services ?? throw new ArgumentNullException(nameof(services));
    }

    protected override async Task<(IReadOnlyList<UserTableObject> Items, int Total)> LoadPageAsync(
        int skip, int take, string? sortKey, bool desc, string? filter, CancellationToken ct)
    {
        var (users, total) = await _db.GetUsersPageAsync(skip, take, sortKey ?? "CreatedAt", desc, filter, ct);
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

        // Refresh on successful save so the row reflects new values.
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

public partial class UserTableObject : ObservableObject
{
    public User User { get; }

    public IAsyncRelayCommand EditCommand { get; }
    public IRelayCommand ViewCommand { get; }

    public UserTableObject(User user, Func<User, Task> onEdit, Action<int> onView)
    {
        User = user ?? throw new ArgumentNullException(nameof(user));
        EditCommand = new AsyncRelayCommand(() => onEdit(User));
        ViewCommand = new RelayCommand(() => onView(User.UserId));
    }
}
