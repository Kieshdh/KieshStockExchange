using CommunityToolkit.Maui.Views;
using KieshStockExchange.ViewModels.AccountViewModels;

namespace KieshStockExchange.Views.AccountPageViews;

public partial class ChangeEmailPage : Popup
{
    private readonly ChangeEmailViewModel _vm;

    public ChangeEmailPage(ChangeEmailViewModel vm)
    {
        InitializeComponent();
        _vm = vm ?? throw new ArgumentNullException(nameof(vm));
        BindingContext = _vm;
        _vm.CloseRequested += OnCloseRequested;
    }

    private void OnCloseRequested(object? sender, EventArgs e)
    {
        // Popup.CloseAsync hops to the UI thread internally, but VM may fire from a
        // background continuation — keep the explicit marshal so we never call into
        // the dispatcher off-thread.
        MainThread.BeginInvokeOnMainThread(async () => await CloseAsync());
    }
}
