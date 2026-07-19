using CommunityToolkit.Maui.Views;
using KieshStockExchange.Helpers;
using KieshStockExchange.ViewModels.AccountViewModels;

namespace KieshStockExchange.Views.AccountPageViews;

public partial class ChangePasswordPage : Popup
{
    private readonly ChangePasswordViewModel _vm;

    public ChangePasswordPage(ChangePasswordViewModel vm)
    {
        InitializeComponent();
        _vm = vm ?? throw new ArgumentNullException(nameof(vm));
        BindingContext = _vm;
        this.WireCloseAndDispose(_vm);
    }
}
