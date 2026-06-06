using KieshStockExchange.ViewModels.TradeViewModels;

namespace KieshStockExchange.Views.TradePageViews;

public partial class TradePage : ContentPage
{
	private readonly TradeViewModel _vm;
    public TradePage(TradeViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        BindingContext = _vm;

        // SegmentedTabView attaches tab 0 before BindingContext propagates.
        OpenOrdersTab.BindingContext         = vm.OpenOrdersVm;
        OrderHistoryTab.BindingContext       = vm.OrderHistoryVm;
        TransactionHistoryTab.BindingContext = vm.TransactionVm;
        PositionsTab.BindingContext          = vm.PositionsVm;

        // The order panel's inner ScrollView reports its full content as desired height, which makes
        // MAUI inflate its star row past the window (clipping the pinned footer). Pin the panel +
        // orderbook to the row's true height ourselves. The calc is loop-free — derived from the page
        // height and the panel-independent nav/symbol-bar heights — so it re-settles on every resize
        // (a sibling-Height binding is circular and overshoots when the window grows).
        RootGrid.SizeChanged   += OnTradeLayoutSizeChanged;
        TopNav.SizeChanged     += OnTradeLayoutSizeChanged;
        SymbolBar.SizeChanged  += OnTradeLayoutSizeChanged;
    }

    private void OnTradeLayoutSizeChanged(object? sender, EventArgs e) => UpdateTradeLayout();

    private void UpdateTradeLayout()
    {
        // Heights: the order panel's inner ScrollView reports its full content as desired height, which
        // makes MAUI inflate its star row past the window (clipping the pinned footer). Pin the panel +
        // orderbook to the row's true height ourselves — loop-free (page height minus the
        // panel-independent nav/symbol-bar), so it re-settles on every resize.
        var avail = RootGrid.Height;
        if (avail > 0)
        {
            // Grid chrome: Padding 8*2 + RowSpacing 8*3 = 40, plus the two Auto rows. Split the
            // remaining height 70/30 (rows are 7*/3*); a few px of safety so a cell never exceeds its row.
            var stars = avail - 40 - TopNav.Height - SymbolBar.Height;
            if (stars > 0)
            {
                var rowHeight  = stars * 0.7 - 4;   // chart / orderbook / order panel row
                var tableHeight = stars * 0.3 - 4;  // bottom tables row
                if (rowHeight >= 1)
                {
                    // Pin the chart too, not just the panel/orderbook: it's the only cell in this row
                    // that otherwise rides the raw star split, so the bottom table's content height
                    // perturbs the 7*/3* allocation and inflates the chart.
                    SetSize(ChartCard, rowHeight, isWidth: false);
                    SetSize(PlaceHost, rowHeight, isWidth: false);
                    SetSize(ModifyHost, rowHeight, isWidth: false);
                    SetSize(OrderBookHost, rowHeight, isWidth: false);
                }
                // Pin the tables row height so its CollectionView content can't drive the star split
                // (vertical twin of the width pin below).
                if (tableHeight >= 1)
                    SetSize(TablesCard, tableHeight, isWidth: false);
            }
        }

        // Width: the bottom tables card is ColumnSpan=3; a `*`-column CollectionView measured unbounded
        // balloons its desired width and inflates the Auto columns, widening the page past the window
        // (clipping the right side, incl. the cancel button). Pin it to the page's inner width so the
        // colspan can't drive the columns. Loop-free (never reads the table's own width).
        var width = RootGrid.Width;
        if (width > 0)
        {
            SetSize(TablesCard, width - 18, isWidth: true); // 16 = Padding 8*2; -2 safety

            // §F10: size the chart COLUMN (col 0) explicitly rather than pinning the chart child's
            // WidthRequest. The chart toolbar's intrinsic min width can keep a `*` column from
            // shrinking, pushing the orderbook (230) + panel (260) past the window so the panel's right
            // edge clips; and pinning the child while it's HorizontalOptions=Fill makes MAUI centre it
            // (uneven gaps between the cards). Setting the column is the right lever — the toolbar clips
            // within it. Loop-free (derived from the window width). Padding 8*2 + ColumnSpacing 8*2 +
            // orderbook 230 + panel 260 (+2 safety). Fall back to `*` on very narrow windows.
            var chartWidth = width - 16 - 16 - 230 - 260 - 2;
            RootGrid.ColumnDefinitions[0].Width = chartWidth >= 100
                ? new GridLength(chartWidth, GridUnitType.Absolute)
                : GridLength.Star;
        }
    }

    private static void SetSize(VisualElement element, double value, bool isWidth)
    {
        if (value < 1) return;
        var current = isWidth ? element.WidthRequest : element.HeightRequest;
        if (Math.Abs(current - value) <= 0.5) return;
        if (isWidth) element.WidthRequest = value; else element.HeightRequest = value;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        // Best-effort load — a failure (server down / mid-reconnect) must not crash the
        // app through the async-void path. Log it, don't throw.
        try { await _vm.InitializeAsync(1); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"TradePage.OnAppearing load failed: {ex}"); }
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        // Cleanup() resets the engine-side selected stock; Dispose() tears
        // down the VM tree so the chart / order book / per-tab VMs don't
        // pile up subscriptions on long-lived singletons. Cleanup first so
        // the engine sees a clean selection before we drop refs to the VMs.
        _vm.Cleanup();
        _vm.Dispose();
    }

}