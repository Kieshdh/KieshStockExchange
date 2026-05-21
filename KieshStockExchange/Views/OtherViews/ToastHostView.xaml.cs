using KieshStockExchange.ViewModels.OtherViewModels;

namespace KieshStockExchange.Views.OtherViews;

public partial class ToastHostView : ContentView
{
    // Resolved from DI rather than constructor-injected so the view can be
    // dropped into XAML on every page without each page needing to plumb a
    // ToastHostVm property. ToastHostViewModel is registered as a singleton
    // so all pages share one inbox of active toasts.
    public ToastHostView()
    {
        InitializeComponent();
        BindingContext = IPlatformApplication.Current?.Services
            .GetRequiredService<ToastHostViewModel>();
    }
}
