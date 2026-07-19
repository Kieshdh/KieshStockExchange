using CommunityToolkit.Maui.Views;
using KieshStockExchange.Helpers;
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
        this.WireCloseAndDispose(_vm);
    }
}
