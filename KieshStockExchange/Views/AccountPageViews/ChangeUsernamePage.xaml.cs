using CommunityToolkit.Maui.Views;
using KieshStockExchange.Helpers;
using KieshStockExchange.ViewModels.AccountViewModels;

namespace KieshStockExchange.Views.AccountPageViews;

public partial class ChangeUsernamePage : Popup
{
    private readonly ChangeUsernameViewModel _vm;

    public ChangeUsernamePage(ChangeUsernameViewModel vm)
    {
        InitializeComponent();
        _vm = vm ?? throw new ArgumentNullException(nameof(vm));
        BindingContext = _vm;
        this.WireCloseAndDispose(_vm);
    }
}
