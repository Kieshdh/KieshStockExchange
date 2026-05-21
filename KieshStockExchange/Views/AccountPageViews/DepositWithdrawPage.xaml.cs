using CommunityToolkit.Maui.Views;
using KieshStockExchange.ViewModels.AccountViewModels;

namespace KieshStockExchange.Views.AccountPageViews;

public partial class DepositWithdrawPage : Popup
{
    private readonly DepositWithdrawViewModel _vm;

    public DepositWithdrawPage(DepositWithdrawViewModel vm)
    {
        InitializeComponent();
        _vm = vm ?? throw new ArgumentNullException(nameof(vm));
        BindingContext = _vm;
        _vm.CloseRequested += OnCloseRequested;
    }

    private void OnCloseRequested(object? sender, EventArgs e) =>
        MainThread.BeginInvokeOnMainThread(async () => await CloseAsync());
}
