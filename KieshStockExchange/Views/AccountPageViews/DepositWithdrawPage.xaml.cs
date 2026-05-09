using KieshStockExchange.ViewModels.AccountViewModels;

namespace KieshStockExchange.Views.AccountPageViews;

public partial class DepositWithdrawPage : ContentPage
{
    private readonly DepositWithdrawViewModel _vm;

    public DepositWithdrawPage(DepositWithdrawViewModel vm)
    {
        InitializeComponent();
        _vm = vm ?? throw new ArgumentNullException(nameof(vm));
        BindingContext = _vm;
        _vm.CloseRequested += OnCloseRequested;
    }

    private void OnCloseRequested(object? sender, EventArgs e)
    {
        // The VM's command runs the deposit on a background thread (ConfigureAwait(false)),
        // so the continuation that fires CloseRequested often lands off the UI thread.
        // Window.CloseWindow is a WinUI call that MUST run on the UI thread or it throws
        // COMException (RPC_E_WRONG_THREAD). Marshal explicitly.
        MainThread.BeginInvokeOnMainThread(() =>
        {
            var win = this.Window;
            if (win != null)
                Application.Current?.CloseWindow(win);
        });
    }
}
