using CommunityToolkit.Maui.Views;
using KieshStockExchange.Helpers;
using KieshStockExchange.ViewModels.AdminViewModels.EditPopups;

namespace KieshStockExchange.Views.AdminPageViews.EditPopups;

public partial class OrderDetailsPopup : Popup
{
    public OrderDetailsViewModel ViewModel { get; }

    public OrderDetailsPopup(OrderDetailsViewModel vm)
    {
        InitializeComponent();
        ViewModel = vm ?? throw new ArgumentNullException(nameof(vm));
        BindingContext = ViewModel;
        this.WireCloseAndDispose(ViewModel);
    }
}
