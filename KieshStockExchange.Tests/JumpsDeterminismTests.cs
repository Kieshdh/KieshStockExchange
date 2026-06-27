using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using KieshStockExchange.Models;
using KieshStockExchange.Services.BackgroundServices.Helpers;
using KieshStockExchange.Services.DataServices.Interfaces;
using KieshStockExchange.Services.MarketEngineServices;
using KieshStockExchange.Services.MarketEngineServices.Interfaces;
using KieshStockExchange.Services.PortfolioServices.Interfaces;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace KieshStockExchange.Tests;

/// <summary>
/// §fat-tail jumps determinism + byte-identical-off. The jump lever submits ORDERS (not per-bot decisions), so
/// the off-path proof is "no source RNG drawn + no orders submitted" (an empty AiUserRngs is necessary-but-
/// insufficient — an MM pass would also pass it). The source is the only randomness; it mirrors RandomShockSource's
/// fixed draw order (arrival → sign → magnitude over a stable stock iteration) so runs reproduce. The burst +
/// aftershock draw NO RNG, so the continuation is replay-stable. Mirrors CoMovement/ImpactDecouple determinism tests.
/// </summary>
public class JumpsDeterminismTests
{
    private static IStockService Stocks(params int[] ids)
    {
        var dict = ids.ToDictionary(i => i, _ => new Stock());
        var m = new Mock<IStockService>();
        m.Setup(s => s.ById).Returns(dict);
        return m.Object;
    }

    // ── Source: pure, seed-reproducible ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Source_reproduces_event_stream_for_same_seed()
    {
        var ids = Enumerable.Range(1, 50).ToArray();
        var a = new RandomJumpSource(Stocks(ids), meanIntervalHours: 0.1, minPct: 0.02, maxPct: 0.05, magnitudeExponent: 1.5);
        var b = new RandomJumpSource(Stocks(ids), meanIntervalHours: 0.1, minPct: 0.02, maxPct: 0.05, magnitudeExponent: 1.5);

        var seenAny = false;
        for (long tick = 1; tick <= 40; tick++)
        {
            var ea = a.Poll(tick, 60.0).ToList();
            var eb = b.Poll(tick, 60.0).ToList();
            Assert.Equal(ea, eb); // JumpEvent is a record struct ⇒ value equality
            seenAny |= ea.Count > 0;
        }
        Assert.True(seenAny, "the schedule should produce at least one arrival so the test actually exercises draws");
    }

    [Fact]
    public void Source_Reset_rewinds_to_the_same_stream()
    {
        var src = new RandomJumpSource(Stocks(Enumerable.Range(1, 30).ToArray()),
            meanIntervalHours: 0.1, minPct: 0.02, maxPct: 0.05, magnitudeExponent: 1.5);

        var first = Enumerable.Range(1, 20).Select(t => src.Poll(t, 60.0).ToList()).ToList();
        src.Reset();
        var second = Enumerable.Range(1, 20).Select(t => src.Poll(t, 60.0).ToList()).ToList();

        for (int i = 0; i < first.Count; i++) Assert.Equal(first[i], second[i]);
    }

    [Fact]
    public void Source_magnitudes_stay_within_band_and_signs_balance_two_sided()
    {
        var src = new RandomJumpSource(Stocks(Enumerable.Range(1, 50).ToArray()),
            meanIntervalHours: 0.05, minPct: 0.02, maxPct: 0.05, magnitudeExponent: 1.5);

        bool sawPos = false, sawNeg = false;
        for (long tick = 1; tick <= 200; tick++)
            foreach (var ev in src.Poll(tick, 60.0))
            {
                double mag = Math.Abs(ev.SignedTargetPct);
                Assert.InRange(mag, 0.02 - 1e-9, 0.05 + 1e-9);
                if (ev.SignedTargetPct > 0) sawPos = true; else sawNeg = true;
            }
        Assert.True(sawPos && sawNeg, "per-event random sign should produce both directions over many arrivals");
    }

    // ── Service: byte-identical when disabled (the real off-path proof) ─────────────────────────────────

    [Fact]
    public async Task Disabled_service_draws_no_rng_and_submits_no_orders()
    {
        // Strict mocks ⇒ ANY call throws. Off-path must touch neither the source nor the entry route.
        var source = new Mock<IJumpSource>(MockBehavior.Strict);
        var entry  = new Mock<IOrderEntryService>(MockBehavior.Strict);
        var books  = new Mock<IOrderBookEngine>(MockBehavior.Strict);
        var accts  = new Mock<IAccountsCache>(MockBehavior.Strict);
        source.Setup(s => s.Reset()); // Reset() reseeds the source (expected + inert); Poll must NOT be called
        JumpsProbe.Configure(false);

        var svc = new JumpService(entry.Object, books.Object, accts.Object, Stocks(1, 2, 3),
            NullLogger<JumpService>.Instance, source.Object,
            enabled: false, aggressorUserId: 20100, maxSlices: 6, slippagePct: 12m,
            aftershockBuckets: 4, aftershockDecay: 0.5, driftGuardPct: 0.10);

        var t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        svc.Reset(t0); // disabled ⇒ clock stays disarmed
        // ctx is never touched on the off-path (guarded return), so null is safe here.
        for (int i = 1; i <= 5; i++)
            await svc.RunAsync(null!, t0.AddSeconds(i), CancellationToken.None);

        source.Verify(s => s.Poll(It.IsAny<long>(), It.IsAny<double>()), Times.Never);
        // strict entry/books/accts mocks already guarantee zero engine/account interaction.
    }

    // ── Probe: counts only when enabled ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Probe_is_silent_when_disabled()
    {
        JumpsProbe.Configure(false);
        JumpsProbe.RecordJump(isBuy: true, realizedPct: 0.04, grossNotional: 1234m);
        JumpsProbe.RecordSuppressed();
        JumpsProbe.RecordAftershock();
        var (fired, suppressed, meanPct, buy, sell, net, gross, after) = JumpsProbe.Drain();
        Assert.Equal((0L, 0L, 0L, 0L, 0L), (fired, suppressed, buy, sell, after));
        Assert.Equal(0.0, meanPct);
        Assert.Equal(0.0, net);
        Assert.Equal(0.0, gross);
    }

    [Fact]
    public void Probe_counts_and_drains_when_enabled()
    {
        JumpsProbe.Configure(true);
        JumpsProbe.Drain(); // clear any residue from other tests
        JumpsProbe.RecordJump(isBuy: true,  realizedPct: 0.04, grossNotional: 1000m);
        JumpsProbe.RecordJump(isBuy: false, realizedPct: 0.02, grossNotional: 400m);
        JumpsProbe.RecordAftershock();

        var (fired, suppressed, meanPct, buy, sell, net, gross, after) = JumpsProbe.Drain();
        Assert.Equal(2L, fired);
        Assert.Equal(0L, suppressed);
        Assert.Equal(1L, buy);
        Assert.Equal(1L, sell);
        Assert.Equal(1L, after);
        Assert.Equal(600.0, net);    // 1000 buy − 400 sell
        Assert.Equal(1400.0, gross); // 1000 + 400
        Assert.Equal(0.03, meanPct, 3); // (0.04 + 0.02) / 2

        var drained = JumpsProbe.Drain(); // second drain is all-zero
        Assert.Equal((0L, 0L, 0L, 0L, 0L), (drained.fired, drained.suppressed, drained.buyEvents, drained.sellEvents, drained.aftershocks));
        JumpsProbe.Configure(false);
    }
}
