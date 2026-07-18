using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KieshStockExchange.Helpers;
using KieshStockExchange.Models;
using KieshStockExchange.Services.BackgroundServices;
using KieshStockExchange.Services.BackgroundServices.Helpers;
using KieshStockExchange.Services.BackgroundServices.Interfaces;
using KieshStockExchange.Services.DataServices.Interfaces;
using KieshStockExchange.ViewModels.OtherViewModels;
using Microsoft.Extensions.Logging;
using System.Collections.ObjectModel;
using System.Linq;
using System.Net.Http;
using System.Text;

namespace KieshStockExchange.ViewModels.AdminViewModels;

public partial class BotDashboardViewModel
{
    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private int _loadedBots;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BotCapDisplay))]
    private int _onlineBots;
    [ObservableProperty] private long _tickCount;
    [ObservableProperty] private long _tradesPlaced;
    [ObservableProperty] private long _failures;
    [ObservableProperty] private string _lastTradeText = "—";
    [ObservableProperty] private string _uptimeText = "—";
    [ObservableProperty] private string _statusText = "Stopped";
    [ObservableProperty] private int? _activeBotCap;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BotCapDisplay))]
    private int? _maxBotCap;

    // "online / max" — the ACTUAL online bot count (matches the activity chart's active
    // series) over the configured cap. Previously showed ActiveBotCap (the scaler's
    // throttle target), which reads 20000/20000 even while far fewer bots are trading.
    public string BotCapDisplay =>
        $"{OnlineBots:N0} / {(MaxBotCap is { } m ? m.ToString("N0") : "∞")}";
    [ObservableProperty] private string _maxBotCapText = string.Empty;
    [ObservableProperty] private int _minBotCap;
    [ObservableProperty] private string _minBotCapText = string.Empty;
    [ObservableProperty] private bool _scalerEnabled;
    [ObservableProperty] private double _tickWorkMsEwma;
    [ObservableProperty] private long _lastTickWorkMicros;
    [ObservableProperty] private double _loadFraction;
    [ObservableProperty] private string _loadFractionText = "—";
    [ObservableProperty] private string _tickLatencyText = "—";
    [ObservableProperty] private string _recentFailuresText = string.Empty;
    [ObservableProperty] private string _failuresByReasonText = string.Empty;
    [ObservableProperty] private string _failuresByStockText = string.Empty;

    // 1s poll of /api/admin/bots/status. On transport failure we keep the old
    // values and surface the error in the status text — never let a 5xx blow
    // up the timer callback.
    private async Task RefreshAsync()
    {
        BotStatusResponse? status;
        try
        {
            status = await _admin.GetStatusAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Bot status poll failed; reusing last snapshot.");
            StatusText = $"Server unreachable ({ex.GetType().Name})";
            return;
        }
        if (status is null) return;
        _lastStatus = status;

        IsRunning = status.IsRunning;
        LoadedBots = status.LoadedBotCount;
        OnlineBots = status.OnlineBotCount;
        TickCount = status.TickCount;
        TradesPlaced = status.TradesPlacedThisSession;
        Failures = status.FailuresThisSession;
        ActiveBotCap = status.ActiveBotCap;
        MaxBotCap = status.MaxBotCap;
        MinBotCap = status.MinBotCap;
        ScalerEnabled = status.AutoScale;
        StatusText = IsRunning ? "Running" : "Stopped";

        var ewmaMs = status.TickWorkMsEwma;
        var lastUs = status.LastTickWorkMicros;
        TickWorkMsEwma = ewmaMs;
        LastTickWorkMicros = lastUs;
        TickLatencyText = ewmaMs > 0
            ? $"{ewmaMs:F1} ms (last {lastUs / 1000.0:F1} ms)"
            : "—";

        var intervalMs = status.TradeIntervalMs;
        LoadFraction = intervalMs > 0 ? ewmaMs / intervalMs : 0;
        LoadFractionText = ewmaMs > 0 ? $"{LoadFraction:P0}" : "—";

        LastTradeText = status.LastTradeAtUtc is { } last
            ? FormatRelative(TimeHelper.NowUtc() - last)
            : "—";
        UptimeText = status.LoopStartedAtUtc is { } started
            ? FormatDuration(TimeHelper.NowUtc() - started)
            : "—";

        RecentFailuresText = BuildRecentFailuresText(status);
        (FailuresByReasonText, FailuresByStockText) = BuildFailureBreakdownTexts(status);

        // Re-seed the editable cap text fields only if the user hasn't typed
        // into them (don't clobber an in-progress edit).
        if (string.IsNullOrWhiteSpace(MaxBotCapText) && status.MaxBotCap is not null)
            MaxBotCapText = status.MaxBotCap.Value.ToString();
        if (string.IsNullOrWhiteSpace(MinBotCapText))
            MinBotCapText = status.MinBotCap.ToString();
    }

    private static string BuildRecentFailuresText(BotStatusResponse status)
    {
        // The server pre-formats one line per failure (timestamp + AIUser +
        // stock + category + message). We just show the visible tail.
        var lines = status.RecentFailures;
        if (lines.Count == 0) return "No recent failures.";
        int take = Math.Min(RecentFailuresDisplayCount, lines.Count);
        int start = lines.Count - take;
        var sb = new StringBuilder(take * 80);
        for (int i = start; i < lines.Count; i++)
            sb.AppendLine(lines[i]);
        return sb.ToString().TrimEnd();
    }

    private (string ByReason, string ByStock) BuildFailureBreakdownTexts(BotStatusResponse status)
    {
        var byCategory = status.FailuresByCategory;
        var byStock    = status.FailuresByStockId;
        if (byCategory.Count == 0 && byStock.Count == 0)
            return ("No failures yet this session.", string.Empty);

        long total = 0;
        foreach (var n in byCategory.Values) total += n;

        var reasonsSb = new StringBuilder(160);
        reasonsSb.Append("By reason (");
        reasonsSb.Append(total.ToString("N0"));
        reasonsSb.AppendLine(" total):");
        var orderedCats = byCategory
            .OrderByDescending(kv => kv.Value)
            .ThenBy(kv => kv.Key);
        foreach (var kv in orderedCats)
        {
            var pct = total > 0 ? (double)kv.Value * 100.0 / total : 0.0;
            reasonsSb.Append("  ").Append(kv.Key)
                     .Append(": ").Append(kv.Value.ToString("N0"))
                     .Append("  (").Append(pct.ToString("F1")).AppendLine("%)");
        }

        string stocksText;
        if (byStock.Count == 0)
        {
            stocksText = string.Empty;
        }
        else
        {
            var stocksSb = new StringBuilder(96);
            stocksSb.Append("Top ").Append(TopStockFailuresCount).AppendLine(" stocks:");
            var topStocks = byStock
                .OrderByDescending(kv => kv.Value)
                .Take(TopStockFailuresCount);
            foreach (var kv in topStocks)
            {
                var symbol = _stocks.TryGetSymbol(kv.Key, out var s) ? s : kv.Key.ToString();
                stocksSb.Append("  ").Append(symbol)
                        .Append(": ").Append(kv.Value.ToString("N0"))
                        .AppendLine();
            }
            stocksText = stocksSb.ToString().TrimEnd();
        }

        return (reasonsSb.ToString().TrimEnd(), stocksText);
    }
}
