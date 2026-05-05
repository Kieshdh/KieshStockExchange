using KieshStockExchange.Services.MarketDataServices;
using KieshStockExchange.Services.MarketDataServices.Interfaces;
using KieshStockExchange.Services.OtherServices.Interfaces;
using KieshStockExchange.ViewModels.TradeViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace KieshStockExchange.Views.TradePageViews;

public partial class ChartView : ContentView
{
    private readonly CandleChartDrawable _drawable = new();
    private ChartViewModel? _vm;
    private readonly IThemeService? _theme;

    // Pan-gesture tracking
    private int _panLastDelta;

    public ChartView()
    {
        InitializeComponent();

        ApplyChartPalette();
        Chart.Drawable = _drawable;
        ChartPan.PanUpdated += OnPanUpdated;

        // Re-pull the chart palette and repaint when the user switches theme,
        // so candles/grid/axis colors follow the active theme dictionary
        // alongside the GraphicsView's DynamicResource-driven BackgroundColor.
        _theme = Application.Current?.Handler?.MauiContext?.Services?.GetService<IThemeService>();
        if (_theme != null)
            _theme.ThemeChanged += OnThemeChanged;
        Unloaded += OnUnloaded;

#if WINDOWS
        Chart.HandlerChanged += OnChartHandlerChanged;
#endif
    }

    private void OnThemeChanged(object? sender, string newKey)
    {
        ApplyChartPalette();
        Chart.Invalidate();
    }

    private void OnUnloaded(object? sender, EventArgs e)
    {
        if (_theme != null) _theme.ThemeChanged -= OnThemeChanged;
    }

    // Pull the palette from Colors.xaml so the drawable doesn't carry its own hex values.
    // Done once in the constructor Ã¢â‚¬â€ these resources are static across the app.
    private void ApplyChartPalette()
    {
        if (TryGetColor("ChartBg",            out var bg))       _drawable.Bg            = bg;
        if (TryGetColor("ChartAxis",          out var axis))     _drawable.Axis          = axis;
        if (TryGetColor("ChartGrid",          out var grid))     _drawable.Grid          = grid;
        if (TryGetColor("ChartBull",          out var bull))     _drawable.Bull          = bull;
        if (TryGetColor("ChartBear",          out var bear))     _drawable.Bear          = bear;
        if (TryGetColor("ChartPriceLineUp",   out var lineUp))   _drawable.PriceLineUp   = lineUp;
        if (TryGetColor("ChartPriceLineDown", out var lineDown)) _drawable.PriceLineDown = lineDown;
    }

    private static bool TryGetColor(string key, out Color color)
    {
        if (Application.Current?.Resources.TryGetValue(key, out var raw) == true && raw is Color c)
        {
            color = c;
            return true;
        }
        color = Colors.Transparent;
        return false;
    }

    protected override void OnBindingContextChanged()
    {
        base.OnBindingContextChanged();

        if (_vm != null)
            _vm.RedrawRequested -= OnRedrawRequested;

        _vm = BindingContext as ChartViewModel;
        if (_vm == null) return;

        _vm.RedrawRequested += OnRedrawRequested;
        UpdateDrawable();
        Chart.Invalidate();
    }

    private void OnRedrawRequested()
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            UpdateDrawable();
            Chart.Invalidate();
        });
    }

    private void UpdateDrawable()
    {
        if (_vm == null) return;
        _drawable.Candles = _vm.GetVisibleCandles();
        _drawable.YPaddingPercent = _vm.YPaddingPercent;
        _drawable.XPaddingPercent = _vm.XPaddingPercent;
        _drawable.CurrentPrice = _vm.GetCurrentPrice();
    }

    private void OnPanUpdated(object? sender, PanUpdatedEventArgs e)
    {
        if (_vm == null) return;
        switch (e.StatusType)
        {
            case GestureStatus.Started:
                _panLastDelta = 0;
                break;

            case GestureStatus.Running:
            {
                double width = Chart.Width;
                int visible = Math.Max(1, _vm.VisibleCount);
                if (width <= 0) return;

                double pxPerCandle = width / visible;
                if (pxPerCandle <= 0) return;

                // Drag right (positive TotalX) = move into history = increase OffsetFromLatest
                int totalDeltaCandles = (int)Math.Round(e.TotalX / pxPerCandle);
                int step = totalDeltaCandles - _panLastDelta;
                if (step != 0 && _vm.PanCommand.CanExecute(step))
                {
                    _vm.PanCommand.Execute(step);
                    _panLastDelta = totalDeltaCandles;
                }
                break;
            }

            case GestureStatus.Completed:
            case GestureStatus.Canceled:
                _panLastDelta = 0;
                break;
        }
    }

#if WINDOWS
    private void OnChartHandlerChanged(object? sender, EventArgs e)
    {
        if (Chart.Handler?.PlatformView is Microsoft.UI.Xaml.UIElement el)
        {
            el.PointerWheelChanged -= OnPointerWheelChanged;
            el.PointerWheelChanged += OnPointerWheelChanged;
        }
    }

    private void OnPointerWheelChanged(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (_vm == null) return;
        var el = (Microsoft.UI.Xaml.UIElement)sender;
        var delta = e.GetCurrentPoint(el).Properties.MouseWheelDelta;
        if (delta == 0) return;

        if (delta > 0)
        {
            if (_vm.ZoomInCommand.CanExecute(null)) _vm.ZoomInCommand.Execute(null);
        }
        else
        {
            if (_vm.ZoomOutCommand.CanExecute(null)) _vm.ZoomOutCommand.Execute(null);
        }
        e.Handled = true;
    }
#endif
}
