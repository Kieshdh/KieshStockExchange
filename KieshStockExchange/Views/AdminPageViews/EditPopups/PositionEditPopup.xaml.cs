using CommunityToolkit.Maui.Views;
using KieshStockExchange.ViewModels.AdminViewModels.EditPopups;

namespace KieshStockExchange.Views.AdminPageViews.EditPopups;

public partial class PositionEditPopup : Popup
{
    public PositionEditViewModel ViewModel { get; }

    public PositionEditPopup(PositionEditViewModel vm)
    {
        InitializeComponent();
        ViewModel = vm ?? throw new ArgumentNullException(nameof(vm));
        BindingContext = ViewModel;
        ViewModel.CloseRequested += OnCloseRequested;
    }

    private void OnCloseRequested(object? sender, EventArgs e)
    {
        MainThread.BeginInvokeOnMainThread(async () => await CloseAsync());
    }
}
