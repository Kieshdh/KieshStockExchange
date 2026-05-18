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
        // CloseWindow must run on the UI thread.
        MainThread.BeginInvokeOnMainThread(() =>
        {
            var win = this.Window;
            if (win != null)
                Application.Current?.CloseWindow(win);
        });
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        _vm.CloseRequested -= OnCloseRequested;
        _vm.Dispose();
    }
}
