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
        var win = this.Window;
        if (win != null)
            Application.Current?.CloseWindow(win);
    }
}
