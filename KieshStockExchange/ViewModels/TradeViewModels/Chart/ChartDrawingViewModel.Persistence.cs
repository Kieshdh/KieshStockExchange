using System.Text.Json;
using KieshStockExchange.Helpers;
using KieshStockExchange.Models;
using KieshStockExchange.Models.ChartDrawing.Objects;
using KieshStockExchange.Models.ChartDrawing.Style;
using KieshStockExchange.Models.ChartDrawing.Tools;
using Microsoft.Extensions.Logging;

namespace KieshStockExchange.ViewModels.TradeViewModels;

// Drawing persistence: serialize the set per stock+currency through IDrawingStore (local cache + debounce
// + server push). Reads sniff the persisted root token so a legacy bare-array blob still loads, then
// normalize style + migrate the legacy arrowhead bool to a line-ending.
public partial class ChartDrawingViewModel
{
    private const string DrawingsPrefKeyBase = "chart_drawings_";
    // Preferences key for the currently loaded stock+currency, or null when nothing is selected.
    private string? _drawingsKey;
    // The (stockId, currency) the current _drawingsKey was built from. PersistDrawings saves under
    // THIS captured identity, never live selection — which may already point at the next stock by the
    // time a queued save runs (both seams run on the threadpool), which would misfile A's drawings
    // under B's key. Also guards the async reconcile against a stale stock-switch.
    private (int StockId, CurrencyType Currency)? _loadedKey;

    // Shared serializer options carrying the Color<->hex converter so DrawStyle round-trips
    // (Maui Color isn't System.Text.Json-serializable on its own).
    private static readonly JsonSerializerOptions _drawingJson = new()
    {
        Converters = { new ColorJsonConverter() },
    };

    // The current persisted-payload schema version. UP-CORE writes the { "v":1, "drawings":[...] }
    // envelope; this "v":1 shape is UP-STORE's client<->server wire contract.
    private const int DrawingsSchemaVersion = 1;

    /// <summary>Serializes the current drawings via the store under the LOADED stock's key.</summary>
    public void PersistDrawings()
    {
        // Save under the captured loaded identity, NOT live selection (which may have moved on).
        if (_drawingsKey is null || _loadedKey is not { } key) return;
        try
        {
            // Always write the v1 envelope (one-way migration on next save). A legacy bare-array blob
            // is read back by ParseSavedDrawings via root-token sniffing. The store owns the
            // local Preferences cache + debounce + server push.
            var envelope = new DrawingEnvelope(DrawingsSchemaVersion, Drawings.ToList());
            _store.Save(key.StockId, key.Currency.ToString(),
                JsonSerializer.Serialize(envelope, _drawingJson));
        }
        catch (Exception ex) { _logger.LogDebug(ex, "Saving chart drawings failed."); }
    }

    // Loads (stockId, currency)'s saved drawings via the store and repopulates Drawings on the UI
    // thread, but only if the selection hasn't moved on and the user hasn't started drawing — so a
    // slow/stale reconcile can never wipe in-progress work or land in the wrong stock's view.
    private async Task LoadDrawingsAsync(int stockId, CurrencyType currency)
    {
        try
        {
            var json = await _store.LoadAsync(stockId, currency.ToString()).ConfigureAwait(false);
            if (string.IsNullOrEmpty(json) || !IsStillLoaded(stockId, currency)) return;

            var parsed = ParseSavedDrawings(json);
            if (parsed is null || parsed.Count == 0) return;

            void Apply()
            {
                // Apply-time re-check: selection unchanged AND nothing drawn/loaded yet.
                if (!IsStillLoaded(stockId, currency) || Drawings.Count > 0) return;
                foreach (var d in parsed) Drawings.Add(d);
            }
            if (MainThread.IsMainThread) Apply();
            else MainThread.BeginInvokeOnMainThread(Apply);
        }
        catch (Exception ex) { _logger.LogDebug(ex, "Loading chart drawings failed."); }
    }

    private bool IsStillLoaded(int stockId, CurrencyType currency)
        => _loadedKey is { } k && k.StockId == stockId && k.Currency == currency;

    // Parse a persisted blob into normalized DrawingObjects. Sniffs the root token (legacy bare array
    // vs the v1 envelope), passing _drawingJson on BOTH branches so the ColorJsonConverter parses
    // "#RRGGBBAA"; applies DrawingBackCompat on the legacy branch, then normalizes style.
    private List<DrawingObject>? ParseSavedDrawings(string json)
    {
        using var doc = JsonDocument.Parse(json);
        bool legacy = doc.RootElement.ValueKind == JsonValueKind.Array;
        List<DrawingObject>? saved =
            legacy
                ? doc.RootElement.Deserialize<List<DrawingObject>>(_drawingJson)
            : doc.RootElement.TryGetProperty("drawings", out var arr)
                ? arr.Deserialize<List<DrawingObject>>(_drawingJson)
                : null;
        if (saved is null) return null;

        var result = new List<DrawingObject>(saved.Count);
        foreach (var raw in saved)
        {
            // A legacy bare-array blob predates the trailing fields, so STJ read them as default(T);
            // re-apply the non-zero defaults (a v1 envelope carries them explicitly).
            var d = legacy ? DrawingBackCompat.ApplyLegacyTrailingDefaults(raw) : raw;
            var style = (d.Style.Color is null || d.Style.Thickness <= 0f)
                ? DrawStyle.Default
                : d.Style;
            // Migrate the legacy arrowhead bool to a line-ending, then retire Arrow on the record.
            if (style.Ending == LineEnding.None && style.Arrow)
                style = style with { Ending = LineEnding.End };
            if (style.Arrow) style = style with { Arrow = false };
            result.Add(d with { Style = style });
        }
        return result;
    }
}
