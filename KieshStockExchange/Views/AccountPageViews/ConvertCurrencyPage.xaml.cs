using KieshStockExchange.ViewModels.AccountViewModels;

namespace KieshStockExchange.Views.AccountPageViews;

public partial class ConvertCurrencyPage : ContentPage
{
    private readonly ConvertCurrencyViewModel _vm;

    public ConvertCurrencyPage(ConvertCurrencyViewModel vm)
    {
        InitializeComponent();
        _vm = vm ?? throw new ArgumentNullException(nameof(vm));
        BindingContext = _vm;
        _vm.CloseRequested += OnCloseRequested;
    }

    private void OnCloseRequested(object? sender, EventArgs e)
    {
        // ConvertAsync awaits the DB call with ConfigureAwait(false), so the
        // CloseRequested continuation can fire off the UI thread. Window.CloseWindow
        // is a WinUI call that must run on the UI thread.
        MainThread.BeginInvokeOnMainThread(() =>
        {
            var win = this.Window;
            if (win != null)
                Application.Current?.CloseWindow(win);
        });
    }
}
