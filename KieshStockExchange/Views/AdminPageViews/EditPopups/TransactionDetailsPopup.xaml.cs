using CommunityToolkit.Maui.Views;
using KieshStockExchange.ViewModels.AdminViewModels;

namespace KieshStockExchange.Views.AdminPageViews.EditPopups;

public partial class TransactionDetailsPopup : Popup
{
    public TransactionDetailsViewModel ViewModel { get; }

    public TransactionDetailsPopup(TransactionDetailsViewModel vm)
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
