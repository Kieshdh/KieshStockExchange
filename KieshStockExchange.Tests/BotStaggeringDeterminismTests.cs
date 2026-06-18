using KieshStockExchange.Services.BackgroundServices;

namespace KieshStockExchange.Tests;

/// <summary>
/// §stagger determinism tests for <see cref="AiTradeService.StaggerDue"/> — the pure tick-phase gate
/// that decides whether a bot is due to act on a given tick. The contract the soak relies on:
///   • Disabled / Slots&lt;=1 ⇒ ALWAYS due ⇒ byte-identical to the un-staggered loop.
///   • ON ⇒ each bot acts on exactly one of every <c>Slots</c> ticks (≈Slots-fold per-tick load cut),
///     a pure function of (AiUserId, TickId) — NO RNG, NO wall-clock — so runs stay reproducible.
/// The method is the whole behavioural surface of slice 1, so pinning it here covers the cadence
/// change without standing up the full bot loop.
/// </summary>
public class BotStaggeringDeterminismTests
{
    [Fact]
    public void Disabled_or_single_slot_is_always_due()
    {
        // Slots <= 1 is the staggering-off / disabled path: every bot is due every tick.
        for (long tick = 0; tick < 16; tick++)
            for (int id = 0; id < 64; id++)
            {
                Assert.True(AiTradeService.StaggerDue(id, tick, 1));
                Assert.True(AiTradeService.StaggerDue(id, tick, 0));
                Assert.True(AiTradeService.StaggerDue(id, tick, -5));
            }
    }

    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(8)]
    public void Each_bot_is_due_exactly_once_per_window(int slots)
    {
        // Over any Slots consecutive ticks a bot acts exactly once — the cadence guarantee.
        for (int id = 0; id < 500; id++)
        {
            int due = 0;
            for (long tick = 0; tick < slots; tick++)
                if (AiTradeService.StaggerDue(id, tick, slots)) due++;
            Assert.Equal(1, due);
        }
    }

    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(5)]
    public void Per_tick_due_count_is_cohort_over_slots(int slots)
    {
        // The load-cut property: each tick sees ~1/Slots of the cohort. Using a cohort divisible by
        // Slots makes the split exact (ids 0..cohort-1 are evenly spread across residue classes).
        const int cohort = 1000;
        for (long tick = 0; tick < slots; tick++)
        {
            int due = 0;
            for (int id = 0; id < cohort; id++)
                if (AiTradeService.StaggerDue(id, tick, slots)) due++;
            Assert.Equal(cohort / slots, due);
        }
    }

    [Fact]
    public void Due_matches_slot_formula_and_is_pure()
    {
        // A bot in slot (id % slots) acts only when (tick % slots) equals its slot; repeated calls
        // with the same inputs return the same answer (no hidden state / RNG ⇒ reproducible).
        var rng = new Random(12345);
        for (int i = 0; i < 5000; i++)
        {
            int id = rng.Next(0, 1_000_000);
            long tick = rng.Next(0, 1_000_000);
            int slots = rng.Next(2, 32);
            bool expected = (tick % slots) == (id % slots);
            bool first = AiTradeService.StaggerDue(id, tick, slots);
            bool second = AiTradeService.StaggerDue(id, tick, slots);
            Assert.Equal(expected, first);
            Assert.Equal(first, second);
        }
    }
}
