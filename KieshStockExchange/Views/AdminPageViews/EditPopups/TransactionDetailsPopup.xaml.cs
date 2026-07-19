using CommunityToolkit.Maui.Views;
using KieshStockExchange.Helpers;
using KieshStockExchange.ViewModels.AdminViewModels.EditPopups;

namespace KieshStockExchange.Views.AdminPageViews.EditPopups;

public partial class TransactionDetailsPopup : Popup
{
    public TransactionDetailsViewModel ViewModel { get; }

    public TransactionDetailsPopup(TransactionDetailsViewModel vm)
    {
        InitializeComponent();
        ViewModel = vm ?? throw new ArgumentNullException(nameof(vm));
        BindingContext = ViewModel;
        this.WireCloseAndDispose(ViewModel);
    }
}
