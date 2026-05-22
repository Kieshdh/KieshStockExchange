using CommunityToolkit.Maui.Views;
using KieshStockExchange.ViewModels.AdminViewModels;

namespace KieshStockExchange.Views.AdminPageViews.EditPopups;

public partial class UserEditPopup : Popup
{
    public UserEditViewModel ViewModel { get; }

    public UserEditPopup(UserEditViewModel vm)
    {
        InitializeComponent();
        ViewModel = vm ?? throw new ArgumentNullException(nameof(vm));
        BindingContext = ViewModel;
        ViewModel.CloseRequested += OnCloseRequested;
    }

    private void OnCloseRequested(object? sender, EventArgs e)
    {
        // VM may fire CloseRequested from a background continuation; marshal
        // explicitly so we never call Popup.CloseAsync off the UI thread.
        MainThread.BeginInvokeOnMainThread(async () => await CloseAsync());
    }
}
