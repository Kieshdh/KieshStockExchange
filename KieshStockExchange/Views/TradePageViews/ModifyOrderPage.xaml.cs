using KieshStockExchange.Models;
using KieshStockExchange.ViewModels.TradeViewModels;

namespace KieshStockExchange.Views.TradePageViews;

public partial class ModifyOrderPage : ContentPage
{
    private readonly ModifyOrderViewModel _vm;

    /// <summary>True while a ModifyOrder modal is currently pushed on the
    /// navigation stack. Lets the open-orders commands skip a second push if
    /// the user double-taps the ✎ button.</summary>
    public static bool IsOpen { get; private set; }

    public ModifyOrderPage(ModifyOrderViewModel vm)
    {
        InitializeComponent();
        _vm = vm ?? throw new ArgumentNullException(nameof(vm));
        BindingContext = _vm;
        _vm.CloseRequested += OnCloseRequested;
    }

    /// <summary>Caller passes the order to edit before the page is pushed.</summary>
    public void Initialize(Order order) => _vm.Initialize(order);

    protected override void OnAppearing()
    {
        base.OnAppearing();
        IsOpen = true;
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        IsOpen = false;
    }

    private void OnCloseRequested(object? sender, EventArgs e)
    {
        // Pop the modal back off the Shell navigation stack on the UI thread.
        // PushModalAsync was used (instead of a separate Window) so the popup
        // sits on top of the Trade page within the same window — no z-order
        // fight when the user hovers back over the chart.
        MainThread.BeginInvokeOnMainThread(async () =>
        {
            try { await Shell.Current.Navigation.PopModalAsync(); }
            catch { /* navigation may already be popping; ignore */ }
        });
    }
}
