using KieshStockExchange.ViewModels.AdminViewModels;
using KieshStockExchange.Services;

namespace KieshStockExchange.Views.AdminPageViews;

public partial class AdminPage : ContentPage
{
    private readonly AdminViewModel _vm;

    // Page chrome above/below the data rows: top nav, tab rail, table header, search
    // row, and pager. Overestimating just yields a slightly shorter page (safe — the
    // CollectionView still scrolls); the MinPageSize floor covers small windows.
    private const double TableChromePx = 210;

    // SizeChanged fires rapidly during a drag-resize; coalesce to the last value.
    private CancellationTokenSource? _resizeCts;

    public AdminPage(AdminViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        BindingContext = vm;

        // SegmentedTabView attaches tab 0 before BindingContext propagates.
        UsersTab.BindingContext        = vm.UsersVm;
        StocksTab.BindingContext       = vm.StocksVm;
        OrdersTab.BindingContext       = vm.OrdersVm;
        TransactionsTab.BindingContext = vm.TransactionsVm;
        FundsTab.BindingContext        = vm.FundsVm;
        PositionsTab.BindingContext    = vm.PositionsVm;
        UserDetailsTab.BindingContext  = vm.UserDetailsVm;
        RefreshButton.Command          = vm.RefreshActiveTabCommand;

        SizeChanged += OnPageSizeChanged;
    }

    private async void OnPageSizeChanged(object? sender, EventArgs e)
    {
        if (Height <= 0) return;

        // Debounce: act ~200ms after the last resize event.
        _resizeCts?.Cancel();
        var cts = _resizeCts = new CancellationTokenSource();
        try
        {
            await Task.Delay(200, cts.Token);
            await _vm.ApplyViewportHeightAsync(Height - TableChromePx);
        }
        catch (OperationCanceledException) { /* superseded by a newer resize */ }
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await _vm.InitializeAsync();
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        _resizeCts?.Cancel(); // drop any pending resize before the VM tree goes away
        // Drop the VM tree so TopNavBarVm releases its handlers on
        // IUserPortfolioService / IUserSessionService / INotificationService.
        _vm.Dispose();
    }
}