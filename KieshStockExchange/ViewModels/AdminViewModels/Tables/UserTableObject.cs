using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KieshStockExchange.Models;
using KieshStockExchange.Services.DataServices.Interfaces;
using KieshStockExchange.Views.AdminPageViews.EditPopups;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using CommunityToolkit.Maui.Views;

namespace KieshStockExchange.ViewModels.AdminViewModels.Tables;

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
