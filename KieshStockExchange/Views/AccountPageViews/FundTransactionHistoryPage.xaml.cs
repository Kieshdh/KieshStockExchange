using CommunityToolkit.Maui.Views;
using KieshStockExchange.Helpers;
using KieshStockExchange.ViewModels.AccountViewModels;

namespace KieshStockExchange.Views.AccountPageViews;

public partial class FundTransactionHistoryPage : Popup
{
    private readonly FundTransactionHistoryViewModel _vm;

    public FundTransactionHistoryPage(FundTransactionHistoryViewModel vm)
    {
        InitializeComponent();
        _vm = vm ?? throw new ArgumentNullException(nameof(vm));
        BindingContext = _vm;
        this.WireCloseAndDispose(_vm);
    }
}
