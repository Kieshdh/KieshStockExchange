using CommunityToolkit.Maui.Views;
using KieshStockExchange.ViewModels.OtherViewModels;

namespace KieshStockExchange.Views.OtherViews;

public partial class InboxPopup : Popup
{
    public InboxPopup(TopNavBarViewModel vm)
    {
        InitializeComponent();
        BindingContext = vm;
    }

    private async void OnCloseClicked(object? sender, EventArgs e)
        => await CloseAsync();
}
