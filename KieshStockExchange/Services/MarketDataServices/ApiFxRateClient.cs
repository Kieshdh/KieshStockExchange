using System.Collections.Concurrent;
using System.Net.Http;
using System.Net.Http.Json;
using KieshStockExchange.Helpers;
using KieshStockExchange.Services.DataServices;
using KieshStockExchange.Services.MarketDataServices.Interfaces;
using Microsoft.Extensions.Logging;

namespace KieshStockExchange.Services.MarketDataServices;

/// <summary>
/// Phase 3 finish — HTTP-backed FX cache. The server owns the AR(1) walk;
/// the client polls /api/fx-rates on a 30s timer and raises RateUpdated when
/// a pair's mid moves. <see cref="GetMidRate"/> and <see cref="GetBidAsk"/>
/// stay synchronous off the local cache so existing VM consumers don't change.
/// </summary>
public sealed class ApiFxRateClient : IFxRateService, IAsyncDisposable
{
    private const decimal ConvertSpread = 0.001m; // matches server FxRateService
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(30);

    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<ApiFxRateClient> _logger;

    private readonly ConcurrentDictionary<(CurrencyType, CurrencyType), decimal> _mids = new();
    private readonly CancellationTokenSource _pollCts = new();
    private Task? _pollLoop;
    private int _started;

    public event EventHandler<FxRateUpdatedEventArgs>? RateUpdated;

    public ApiFxRateClient(IHttpClientFactory httpFactory, ILogger<ApiFxRateClient> logger)
    {
        _httpFactory = httpFactory;
        _logger = logger;
    }

    public decimal GetMidRate(CurrencyType from, CurrencyType to)
    {
        EnsurePollStarted();
        if (from == to) return 1m;
        if (_mids.TryGetValue((from, to), out var mid) && mid > 0m) return mid;
        if (_mids.TryGetValue((to, from), out var inverse) && inverse > 0m) return 1m / inverse;
        // Cold path before the first poll completes — fall back to the static
        // conversion table so callers don't see 0.
        return CurrencyHelper.Convert(1m, from, to, decimals: 6);
    }

    public (decimal Bid, decimal Ask) GetBidAsk(CurrencyType from, CurrencyType to)
    {
        var mid = GetMidRate(from, to);
        return (mid * (1m - ConvertSpread), mid * (1m + ConvertSpread));
    }

    public void Reset() { /* server-owned */ }
    public void Tick(DateTime now) { /* server-owned */ }

    private void EnsurePollStarted()
    {
        if (Interlocked.Exchange(ref _started, 1) != 0) return;
        _pollLoop = Task.Run(() => PollLoopAsync(_pollCts.Token));
    }

    private async Task PollLoopAsync(CancellationToken ct)
    {
        var http = _httpFactory.CreateClient("KSE.Server");
        // Initial fetch up front so subscribers don't see the static fallback
        // for the full PollInterval after startup.
        await RefreshOnceAsync(http, ct).ConfigureAwait(false);
        using var timer = new PeriodicTimer(PollInterval);
        while (!ct.IsCancellationRequested)
        {
            try { await timer.WaitForNextTickAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            await RefreshOnceAsync(http, ct).ConfigureAwait(false);
        }
    }

    private async Task RefreshOnceAsync(HttpClient http, CancellationToken ct)
    {
        try
        {
            var list = await http.GetFromJsonAsync<List<FxRateDto>>("api/fx-rates", ApiJsonOptions.Default, ct).ConfigureAwait(false);
            if (list is null) return;
            foreach (var dto in list)
            {
                var key = (dto.From, dto.To);
                _mids.TryGetValue(key, out var old);
                _mids[key] = dto.Mid;
                if (old > 0m && old != dto.Mid)
                {
                    try { RateUpdated?.Invoke(this, new FxRateUpdatedEventArgs(dto.From, dto.To, old, dto.Mid)); }
                    catch (Exception ex) { _logger.LogWarning(ex, "RateUpdated subscriber threw for {From}->{To}", dto.From, dto.To); }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "FX rate refresh failed; keeping existing cache.");
        }
    }

    public async ValueTask DisposeAsync()
    {
        _pollCts.Cancel();
        if (_pollLoop is not null) { try { await _pollLoop.ConfigureAwait(false); } catch { } }
        _pollCts.Dispose();
    }

    // Wire-shape mirror of the server's FxRateController response.
    private sealed record FxRateDto(CurrencyType From, CurrencyType To, decimal Mid);
}
