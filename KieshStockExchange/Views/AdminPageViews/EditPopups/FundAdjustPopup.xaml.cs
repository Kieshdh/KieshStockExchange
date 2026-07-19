using CommunityToolkit.Maui.Views;
using KieshStockExchange.Helpers;
using KieshStockExchange.ViewModels.AdminViewModels.EditPopups;

namespace KieshStockExchange.Views.AdminPageViews.EditPopups;

public partial class FundAdjustPopup : Popup
{
    public FundAdjustViewModel ViewModel { get; }

    public FundAdjustPopup(FundAdjustViewModel vm)
    {
        InitializeComponent();
        ViewModel = vm ?? throw new ArgumentNullException(nameof(vm));
        BindingContext = ViewModel;
        this.WireCloseAndDispose(ViewModel);
    }
}
