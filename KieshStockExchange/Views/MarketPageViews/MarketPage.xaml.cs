using KieshStockExchange.ViewModels.MarketViewModels;

namespace KieshStockExchange.Views.MarketPageViews;

public partial class MarketPage : ContentPage
{
    private readonly MarketViewModel _vm;

    public MarketPage(MarketViewModel vm)
    {
        InitializeComponent();
        _vm = vm ?? throw new ArgumentNullException(nameof(vm));
        BindingContext = _vm;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        // Subscribe-all + first poll, then start the 5 s timer. Best-effort — a load
        // failure must not crash the app through the async-void path.
        try
        {
            if (_vm.RefreshCommand.CanExecute(null))
                await _vm.RefreshCommand.ExecuteAsync(null);
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"MarketPage.OnAppearing load failed: {ex}"); }
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        // Pause polling without tearing down the row cache, so coming back
        // to this page reads from warm state instead of cold-starting the
        // subscribe + first-poll loop. The VM Dispose() is intentionally
        // NOT called here — DI owns its lifetime.
        _vm.PausePolling();
    }
}
