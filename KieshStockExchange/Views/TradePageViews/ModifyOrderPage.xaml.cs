using KieshStockExchange.Models;
using KieshStockExchange.ViewModels.TradeViewModels;

namespace KieshStockExchange.Views.TradePageViews;

public partial class ModifyOrderPage : ContentPage
{
    private readonly ModifyOrderViewModel _vm;

    public ModifyOrderPage(ModifyOrderViewModel vm)
    {
        InitializeComponent();
        _vm = vm ?? throw new ArgumentNullException(nameof(vm));
        BindingContext = _vm;
        _vm.CloseRequested += OnCloseRequested;
    }

    /// <summary>Caller passes the order to edit before the window opens.</summary>
    public void Initialize(Order order) => _vm.Initialize(order);

    private void OnCloseRequested(object? sender, EventArgs e)
    {
        // Window.CloseWindow is a WinUI call that must run on the UI thread; the VM
        // may fire CloseRequested from a background continuation, so marshal explicitly.
        MainThread.BeginInvokeOnMainThread(() =>
        {
            var win = this.Window;
            if (win != null)
                Application.Current?.CloseWindow(win);
        });
    }
}
