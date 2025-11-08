// Views/TradePageViews/ChartView.xaml.cs
using System.Collections.Specialized;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Graphics;
using KieshStockExchange.ViewModels.TradeViewModels;
using KieshStockExchange.Helpers;

namespace KieshStockExchange.Views.TradePageViews;

public partial class ChartView : ContentView
{
    private CandleChartDrawable _drawable = new();
    private ChartViewModel? _vm;

    public ChartView()
    {
        InitializeComponent();

        Chart.Drawable = _drawable;

        // local UI controls push into the VM
        CandlesStepper.ValueChanged += (_, e) =>
        {
            if (_vm != null) _vm.AmountOfCandlesToShow = (int)e.NewValue;
        };
        YPadSlider.ValueChanged += (_, e) =>
        {
            if (_vm != null) _vm.YPaddingPercent = e.NewValue;
        };
    }

    protected override void OnBindingContextChanged()
    {
        base.OnBindingContextChanged();

        if (_vm != null)
        {
            // detach old
            _vm.RedrawRequested -= OnRedrawRequested;
        }

        _vm = BindingContext as ChartViewModel;
        if (_vm == null) return;

        // push current VM values into the UI widgets
        CandlesStepper.Value = _vm.AmountOfCandlesToShow;
        YPadSlider.Value = _vm.YPaddingPercent;

        _vm.RedrawRequested += OnRedrawRequested;

        // First draw
        UpdateDrawable();
        Chart.Invalidate();
    }

    private void OnRedrawRequested()
    {
        UpdateDrawable();
        MainThread.BeginInvokeOnMainThread(Chart.Invalidate);
    }

    private void UpdateDrawable()
    {
        if (_vm == null) return;
        _drawable.Candles = _vm.GetVisibleCandles();
        _drawable.YPaddingPercent = _vm.YPaddingPercent;
    }
}
