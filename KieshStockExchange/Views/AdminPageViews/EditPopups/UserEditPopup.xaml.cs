using CommunityToolkit.Maui.Views;
using KieshStockExchange.Helpers;
using KieshStockExchange.ViewModels.AdminViewModels.EditPopups;

namespace KieshStockExchange.Views.AdminPageViews.EditPopups;

public partial class UserEditPopup : Popup
{
    public UserEditViewModel ViewModel { get; }

    public UserEditPopup(UserEditViewModel vm)
    {
        InitializeComponent();
        ViewModel = vm ?? throw new ArgumentNullException(nameof(vm));
        BindingContext = ViewModel;
        this.WireCloseAndDispose(ViewModel);
    }
}
