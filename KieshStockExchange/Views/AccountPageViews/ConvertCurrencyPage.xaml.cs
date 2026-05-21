using CommunityToolkit.Maui.Core;
using CommunityToolkit.Maui.Views;
using KieshStockExchange.ViewModels.AccountViewModels;

namespace KieshStockExchange.Views.AccountPageViews;

public partial class ConvertCurrencyPage : Popup
{
    private readonly ConvertCurrencyViewModel _vm;

    public ConvertCurrencyPage(ConvertCurrencyViewModel vm)
    {
        InitializeComponent();
        _vm = vm ?? throw new ArgumentNullException(nameof(vm));
        BindingContext = _vm;
        _vm.CloseRequested += OnCloseRequested;
        Closed += OnPopupClosed;
    }

    private void OnCloseRequested(object? sender, EventArgs e) =>
        MainThread.BeginInvokeOnMainThread(async () => await CloseAsync());

    private void OnPopupClosed(object? sender, PopupClosedEventArgs e)
    {
        _vm.CloseRequested -= OnCloseRequested;
        Closed -= OnPopupClosed;
        _vm.Dispose();
    }
}
