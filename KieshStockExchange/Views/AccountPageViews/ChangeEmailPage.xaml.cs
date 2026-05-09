using KieshStockExchange.ViewModels.AccountViewModels;

namespace KieshStockExchange.Views.AccountPageViews;

public partial class ChangeEmailPage : ContentPage
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
        // CloseWindow is a WinUI call that requires the UI thread; the VM may fire this
        // event from a background continuation (ConfigureAwait(false) on the DB call).
        MainThread.BeginInvokeOnMainThread(() =>
        {
            var win = this.Window;
            if (win != null)
                Application.Current?.CloseWindow(win);
        });
    }
}
