namespace KieshStockExchange.Services.BackgroundServices.Helpers;

/// <summary>
/// §refill-throttle (Bots:RefillThrottle): the bot-decision-layer "refill RESPONSE" lever. The realism
/// ceiling is the fast-refilling order book — ~20k bots re-post resting limits at the OLD level every ~1s
/// tick, so the instant a taker eats the resisting wall another bot rebuilds it and the mid never moves
/// (jump probe: a 4–7% jump realizes ~0.1%). This gate makes refill RESPOND to a confirmed mover: on a
/// stock with strong directional pressure it (a) widens the resisting side's limit offset and/or (b) skips
/// re-posting the resisting-side limit, so the wall the move is pushing into stops instantly reforming.
///
/// It is a BOUNDED control loop, deliberately NOT the open-loop "just remove the wall" design (that was the
/// BuyStopFraction failure mode — it traded a drift for an uncontrolled creep). Three guards keep it stable:
///   • a fill-derived, self-extinguishing SIGNAL (realized return) — not the circular sentiment slope the
///     bots themselves consume; once the mid jumps, realized return falls back and the throttle relaxes;
///   • a Schmitt trigger (arm high / disarm low + a re-arm cooldown) so it does not chatter or re-latch the
///     instant a move ends;
///   • a per-event MOVE-BUDGET CAP — once cumulative displacement since arming reaches MaxEventMovePct
///     (the 4–7% target band) the gate force-disarms so the wall reforms and the move arrests.
///
/// CK-safe by construction (fewer/wider resting orders create or destroy nothing — bot-decision layer only,
/// no matching/settlement change) and perf-neutral-to-positive (fewer resting orders on movers). The gate
/// instance exists ONLY when Bots:RefillThrottle:Enabled is true; when off the context holds a null gate, so
/// every call site is byte-identical and consumes no RNG. The single skip-repost RNG draw is taken LAST and
/// ONLY when the gate is enabled AND the order resists the move, so the flag-off draw stream is unchanged.
/// Loop-thread-only ⇒ plain Dictionary state, no locks.
/// </summary>
internal sealed class RefillThrottleGate
{
    internal enum SignalSource { RealizedReturnFast, BookImbalance, SentimentSlopeFast }

    internal readonly struct Settings
    {
        public bool          Enabled            { get; init; }
        public SignalSource  Source             { get; init; }
        public decimal       ThresholdArm       { get; init; }
        public decimal       ThresholdDisarm    { get; init; }
        public decimal       MaxEventMovePct    { get; init; }
        public long          RearmCooldownTicks { get; init; }
        public decimal       OffsetWidenMult    { get; init; }
        public decimal       SkipRepostProb     { get; init; }
    }

    private readonly struct StockState
    {
        public sbyte   Armed       { get; init; }   // 0 idle, +1 up-mover, -1 down-mover
        public decimal StartPrice  { get; init; }   // price when the current event armed
        public long    LastDisarm  { get; init; }   // tick of the most recent disarm (for re-arm cooldown)
    }

    private readonly Settings _s;
    private readonly Dictionary<int, StockState> _state = new();

    internal RefillThrottleGate(Settings s) => _s = s;

    internal Settings Config => _s;
    internal SignalSource Source => _s.Source;
    internal bool SkipRepostEnabled => _s.SkipRepostProb > 0m;

    /// <summary>
    /// Advance the per-stock control loop and return the effective mover state: sign ∈ {-1,0,+1} and an
    /// intensity ∈ [0,1]. Today intensity is 1 above threshold (byte-identical to a tri-state); it is a seam
    /// for a future per-stock "refill-intensity" platform sourced from the shared market factor. Pure given
    /// (signal, price, tick); the only mutation is this stock's own latch state.
    /// </summary>
    internal (sbyte sign, decimal intensity) Step(int stockId, decimal signal, decimal price, long tick)
    {
        _state.TryGetValue(stockId, out var st);
        decimal mag = Math.Abs(signal);
        sbyte sig = signal > 0m ? (sbyte)1 : signal < 0m ? (sbyte)-1 : (sbyte)0;

        if (st.Armed == 0)
        {
            // Idle → arm only on a strong signal, and not while the re-arm cooldown is still cooling.
            bool cooling = _s.RearmCooldownTicks > 0 && st.LastDisarm > 0 &&
                           (tick - st.LastDisarm) < _s.RearmCooldownTicks;
            if (!cooling && sig != 0 && mag >= _s.ThresholdArm)
                st = new StockState { Armed = sig, StartPrice = price, LastDisarm = st.LastDisarm };
        }
        else
        {
            // Active → disarm on (i) move-budget exhausted, (ii) signal decayed below the disarm threshold,
            // or (iii) the signal flipping the other way.
            bool budgetDone = _s.MaxEventMovePct > 0m && st.StartPrice > 0m &&
                              Math.Abs(price - st.StartPrice) / st.StartPrice >= _s.MaxEventMovePct;
            bool decayed    = mag < _s.ThresholdDisarm;
            bool flipped    = sig != 0 && sig != st.Armed;
            if (budgetDone || decayed || flipped)
                st = new StockState { Armed = 0, StartPrice = 0m, LastDisarm = tick };
        }

        _state[stockId] = st;
        return (st.Armed, st.Armed != 0 ? 1m : 0m);
    }

    /// <summary>True when an order on this side is the wall the move is pushing INTO: an up-mover (+1) is
    /// resisted by SELL/ask limits; a down-mover (-1) by BUY/bid limits.</summary>
    internal static bool ResistsMove(bool isBuy, sbyte sign)
        => (sign > 0 && !isBuy) || (sign < 0 && isBuy);

    /// <summary>Resisting-side offset multiplier: 1.0 (no-op) unless this order resists an armed mover, else
    /// 1 + OffsetWidenMult·intensity. Pure math, no RNG ⇒ Mult 0 reproduces today's offset exactly.</summary>
    internal decimal WidenFactor(bool isBuy, sbyte sign, decimal intensity)
        => ResistsMove(isBuy, sign) ? 1m + _s.OffsetWidenMult * intensity : 1m;
}
