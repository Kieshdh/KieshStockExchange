using KieshStockExchange.ViewModels.AccountViewModels;

namespace KieshStockExchange.Views.AccountPageViews;

public partial class ChangePasswordPage : ContentPage
{
    private readonly ChangePasswordViewModel _vm;

    public ChangePasswordPage(ChangePasswordViewModel vm)
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
