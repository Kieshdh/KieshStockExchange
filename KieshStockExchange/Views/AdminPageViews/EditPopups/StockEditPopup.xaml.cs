using CommunityToolkit.Maui.Views;
using KieshStockExchange.Helpers;
using KieshStockExchange.ViewModels.AdminViewModels.EditPopups;

namespace KieshStockExchange.Views.AdminPageViews.EditPopups;

public partial class StockEditPopup : Popup
{
    public StockEditViewModel ViewModel { get; }

    public StockEditPopup(StockEditViewModel vm)
    {
        InitializeComponent();
        ViewModel = vm ?? throw new ArgumentNullException(nameof(vm));
        BindingContext = ViewModel;
        this.WireCloseAndDispose(ViewModel);
    }
}
