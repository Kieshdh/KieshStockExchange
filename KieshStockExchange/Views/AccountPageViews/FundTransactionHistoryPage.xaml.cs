using KieshStockExchange.ViewModels.AccountViewModels;

namespace KieshStockExchange.Views.AccountPageViews;

public partial class FundTransactionHistoryPage : ContentPage
{
    private readonly FundTransactionHistoryViewModel _vm;

    public FundTransactionHistoryPage(FundTransactionHistoryViewModel vm)
    {
        InitializeComponent();
        _vm = vm ?? throw new ArgumentNullException(nameof(vm));
        BindingContext = _vm;
        _vm.CloseRequested += OnCloseRequested;
    }

    private void OnCloseRequested(object? sender, EventArgs e)
    {
        // Window.CloseWindow is a WinUI call that must run on the UI thread; the VM
        // may fire CloseRequested from a background continuation, so marshal explicitly.
        MainThread.BeginInvokeOnMainThread(() =>
        {
            var win = this.Window;
            if (win != null)
                Application.Current?.CloseWindow(win);
        });
    }
}
