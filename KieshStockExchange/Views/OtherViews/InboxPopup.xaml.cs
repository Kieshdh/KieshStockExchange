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
    {
        // async void — a failed close must not crash the app.
        try { await CloseAsync(); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"InboxPopup.OnCloseClicked failed: {ex}"); }
    }
}
