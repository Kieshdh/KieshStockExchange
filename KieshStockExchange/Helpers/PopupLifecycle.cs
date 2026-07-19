using CommunityToolkit.Maui.Core;
using CommunityToolkit.Maui.Views;

namespace KieshStockExchange.Helpers;

// A popup VM that raises CloseRequested and owns disposable subscriptions.
public interface IClosablePopupViewModel : IDisposable
{
    event EventHandler? CloseRequested;
}

// Shared popup close+dispose wiring so a VM's event can't pin the popup after it closes.
public static class PopupLifecycle
{
    public static void WireCloseAndDispose(this Popup popup, IClosablePopupViewModel vm)
    {
        // Local functions so OnClosed can unsubscribe both handlers by name.
        void OnCloseRequested(object? s, EventArgs e) =>
            MainThread.BeginInvokeOnMainThread(async () => await popup.CloseAsync());

        void OnClosed(object? s, PopupClosedEventArgs e)
        {
            vm.CloseRequested -= OnCloseRequested;
            popup.Closed -= OnClosed;
            vm.Dispose();
        }

        vm.CloseRequested += OnCloseRequested;
        popup.Closed += OnClosed;
    }
}
