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
    [ObservableProperty] private string _exportFailuresStatusText = string.Empty;
    [ObservableProperty] private string _exportLedgerStatusText = string.Empty;
    [ObservableProperty] private string _exportEconomyStatusText = string.Empty;
    [ObservableProperty] private string _exportSentimentStatusText = string.Empty;

    [RelayCommand]
    private Task ExportFailuresAsync() =>
        DownloadServerCsvAsync("api/admin/bots/failures.csv", $"bot_failures_{TimeHelper.NowUtc():yyyyMMdd_HHmmss}",
            "failure rows", v => ExportFailuresStatusText = v);

    // Clears the server's failure ring + persisted NDJSON, then refreshes so the list empties immediately.
    [RelayCommand]
    private async Task ClearFailuresAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        try
        {
            await _admin.ClearFailuresAsync().ConfigureAwait(false);
            ExportFailuresStatusText = "Failures cleared.";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to clear bot failures.");
            ExportFailuresStatusText = $"Clear failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
            await RefreshAsync().ConfigureAwait(false);
        }
    }

    [RelayCommand]
    private Task ExportEconomyAsync() =>
        DownloadServerCsvAsync("api/admin/bots/economy.csv", $"bot_economy_{TimeHelper.NowUtc():yyyyMMdd_HHmmss}",
            "economy samples", v => ExportEconomyStatusText = v);

    [RelayCommand]
    private Task ExportSentimentAsync() =>
        DownloadServerCsvAsync("api/admin/bots/sentiment.csv", $"bot_sentiment_{TimeHelper.NowUtc():yyyyMMdd_HHmmss}",
            "sentiment rows", v => ExportSentimentStatusText = v);

    [RelayCommand]
    private Task ExportLedgerAsync() =>
        DownloadServerCsvAsync("api/admin/bots/reservation-ledger.csv", $"reservation_ledger_{TimeHelper.NowUtc():yyyyMMdd_HHmmss}",
            "ledger rows", v => ExportLedgerStatusText = v);

    // Phase 3 follow-up: data lives in the server's ringbuffers now. Pull the
    // CSV body over HTTP, ask the user where to save it via the existing
    // platform picker, then write the body locally. Row count comes from the
    // body itself (line count minus header) so we don't need a separate counts
    // round-trip per export.
    private async Task DownloadServerCsvAsync(string serverPath, string suggestedFileName,
        string rowLabel, Action<string> setStatus)
    {
        if (IsBusy) return;
        IsBusy = true;
        try
        {
            var savePath = await PickFailureExportPathAsync(suggestedFileName).ConfigureAwait(false);
            if (string.IsNullOrEmpty(savePath))
            {
                setStatus("Export cancelled.");
                return;
            }

            using var resp = await _http.GetAsync(serverPath).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            var csv = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            await File.WriteAllTextAsync(savePath, csv).ConfigureAwait(false);

            // Header line + N data lines. Trailing newline is fine — Count('\n') still
            // gives header + data; subtract one for the header itself.
            var lineCount = 0;
            for (int i = 0; i < csv.Length; i++) if (csv[i] == '\n') lineCount++;
            var rowCount = Math.Max(0, lineCount - 1);

            setStatus($"Exported {rowCount:N0} {rowLabel} to {savePath}");
            _logger.LogInformation("CSV exported from {ServerPath} → {Local}", serverPath, savePath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to download CSV from {ServerPath}", serverPath);
            setStatus($"Export failed: {ex.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    // Windows-only save dialog; returns null on other platforms.
    private static async Task<string?> PickFailureExportPathAsync(string suggestedFileName)
    {
#if WINDOWS
        var picker = new Windows.Storage.Pickers.FileSavePicker
        {
            SuggestedFileName = suggestedFileName,
            SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.DocumentsLibrary,
        };
        picker.FileTypeChoices.Add("CSV file", new List<string> { ".csv" });

        // WinUI 3 requires HWND parenting or PickSaveFileAsync throws E_FAIL.
        var window = Microsoft.Maui.Controls.Application.Current?.Windows.FirstOrDefault();
        if (window?.Handler?.PlatformView is Microsoft.UI.Xaml.Window winuiWindow)
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(winuiWindow);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
        }

        var file = await picker.PickSaveFileAsync();
        return file?.Path;
#else
        await Task.CompletedTask;
        return null;
#endif
    }

    [RelayCommand]
    private async Task StartBotsAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        try
        {
            await _admin.StartAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start bots from dashboard.");
        }
        finally
        {
            IsBusy = false;
            await RefreshAsync().ConfigureAwait(false);
        }
    }

    [RelayCommand]
    private async Task StopBotsAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        try
        {
            await _admin.StopAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to stop bots from dashboard.");
        }
        finally
        {
            IsBusy = false;
            await RefreshAsync().ConfigureAwait(false);
        }
    }

    [RelayCommand]
    private async Task ApplyMaxBotCapAsync()
    {
        int? cap;
        if (string.IsNullOrWhiteSpace(MaxBotCapText))
            cap = null;
        else if (int.TryParse(MaxBotCapText.Trim(), out var n) && n >= 0)
            cap = n;
        else
        {
            _logger.LogInformation("Invalid max bot cap input: {Text}", MaxBotCapText);
            return;
        }

        try
        {
            await _admin.UpdateScalerAsync(new BotScalerSettings(ActiveCap: null, MaxCap: cap, MinCap: null, AutoScale: null))
                .ConfigureAwait(false);
        }
        catch (Exception ex) { _logger.LogError(ex, "Failed to update max bot cap."); }
        await RefreshAsync().ConfigureAwait(false);
    }

    [RelayCommand]
    private async Task ApplyMinBotCapAsync()
    {
        if (!int.TryParse(MinBotCapText?.Trim(), out var n) || n < 0)
        {
            _logger.LogInformation("Invalid min bot cap input: {Text}", MinBotCapText);
            return;
        }

        try
        {
            await _admin.UpdateScalerAsync(new BotScalerSettings(ActiveCap: null, MaxCap: null, MinCap: n, AutoScale: null))
                .ConfigureAwait(false);
        }
        catch (Exception ex) { _logger.LogError(ex, "Failed to update min bot cap."); }
        await RefreshAsync().ConfigureAwait(false);
    }

    partial void OnScalerEnabledChanged(bool value)
    {
        // Fire and forget — the dashboard's poll picks up the new state on the
        // next tick. We don't await here because the OnXxxChanged partial is
        // synchronous and called from the property setter path.
        _ = _admin.UpdateScalerAsync(new BotScalerSettings(ActiveCap: null, MaxCap: null, MinCap: null, AutoScale: value));
    }
}
