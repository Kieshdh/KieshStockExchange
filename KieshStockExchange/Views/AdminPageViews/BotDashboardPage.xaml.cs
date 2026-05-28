using KieshStockExchange.Helpers;
using KieshStockExchange.ViewModels.AdminViewModels;

namespace KieshStockExchange.Views.AdminPageViews;

public partial class BotDashboardPage : ContentPage
{
    private static readonly Color TradesColor = Color.FromArgb("#34D399"); // BuyGreen
    private static readonly Color VolumeColor = Color.FromArgb("#F59E0B"); // Primary
    private static readonly Color ActiveColor = Color.FromArgb("#60A5FA"); // muted blue

    private readonly BotDashboardViewModel _vm;
    private readonly BotSparklineDrawable _drawable = new();

    public BotDashboardPage(BotDashboardViewModel vm)
    {
        InitializeComponent();
        _vm = vm ?? throw new ArgumentNullException(nameof(vm));
        BindingContext = _vm;

        // Theme-aware axis + grid colors (mirrors the trade-page chart).
        if (TryGetColor("ChartAxis", out var axis)) _drawable.AxisColor = axis;
        if (TryGetColor("Divider",   out var grid)) _drawable.GridColor = grid;

        ActivityChart.Drawable = _drawable;
        _vm.ActivityRefreshed += OnActivityRefreshed;
    }

    private void OnActivityRefreshed(object? sender, EventArgs e)
    {
        switch (_vm.SeriesIndex)
        {
            case 1:
                _drawable.Values = _vm.ActivityVolumeSeries;
                _drawable.LineColor = VolumeColor;
                _drawable.ValueFormatter = v => CurrencyHelper.FormatCompact((decimal)v, CurrencyType.USD);
                break;
            case 2:
                _drawable.Values = _vm.ActivityActiveSeries;
                _drawable.LineColor = ActiveColor;
                _drawable.ValueFormatter = v => v.ToString("F0");
                break;
            default:
                _drawable.Values = _vm.ActivityTradesSeries;
                _drawable.LineColor = TradesColor;
                _drawable.ValueFormatter = v => v.ToString("F0");
                break;
        }

        var rangeIdx = Math.Clamp(_vm.ActivityRangeIndex, 0, _vm.ActivityRangeMinutes.Count - 1);
        _drawable.TimeRange = TimeSpan.FromMinutes(_vm.ActivityRangeMinutes[rangeIdx]);
        _drawable.EndTime = DateTime.Now;
        ActivityChart.Invalidate();
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

    protected override void OnAppearing()
    {
        base.OnAppearing();
        _vm.StartPolling();
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        // Unhook the page-level chart-refresh handler and tear down the VM.
        // Dispose calls StopPolling internally + disposes TopNavBarVm so
        // its singleton-service subscriptions don't accumulate per visit.
        _vm.ActivityRefreshed -= OnActivityRefreshed;
        _vm.Dispose();
    }
}
