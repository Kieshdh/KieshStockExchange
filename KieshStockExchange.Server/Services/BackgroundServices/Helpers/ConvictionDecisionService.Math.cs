using KieshStockExchange.Helpers;
using KieshStockExchange.Models;
using KieshStockExchange.Services.DataServices;
using KieshStockExchange.Services.DataServices.Interfaces;
using KieshStockExchange.Services.MarketEngineServices;
using KieshStockExchange.Services.MarketEngineServices.CommandDtos;
using KieshStockExchange.Services.MarketEngineServices.Interfaces;
using KieshStockExchange.Services.PortfolioServices.Interfaces;
using Microsoft.Extensions.Logging;

namespace KieshStockExchange.Services.BackgroundServices.Helpers;

internal sealed partial class ConvictionDecisionService
{
    /// <summary>A per-bot hashed dial in [lo, hi): lo + (hi−lo)·HashUnit01(id, salt). Deterministic, RNG-free.</summary>
    internal static double Dial(int aiUserId, int salt, double lo, double hi)
        => lo + (hi - lo) * BotMath.HashUnit01(aiUserId, salt);

    /// <summary>Per-bot lean: chaser(+1) with probability <paramref name="chaserProb"/>, else fader(−1).</summary>
    internal static int Lean(int aiUserId, double chaserProb)
        => BotMath.HashUnit01(aiUserId, LeanSalt) < chaserProb ? 1 : -1;

    /// <summary>The conviction score. Sentiment + sector-momentum LED (a fader NEGATES those two terms); the shared
    /// global signal and the personal idio term add heterogeneity; the −Wover overvaluation term is a one-way VETO
    /// (only subtracts, never pushes a buy) against chasing a name already above the bank estimate. Pure ⇒ testable.</summary>
    internal static double Hot(double sectorSent, double mom, double global, double idio, double gap,
        int lean, double wSec, double wMom, double wGlobal, double wIdio, double wOver)
    {
        double leanF = lean >= 0 ? 1.0 : -1.0;
        double over  = Math.Max(0.0, -gap);      // overvaluation = (price − est)/est when price is above the estimate
        return wSec * leanF * sectorSent
             + wMom * leanF * mom
             + wGlobal * global
             + wIdio * idio
             - wOver * over;
    }

    /// <summary>Entry gate: conviction clears the sensitivity-scaled bar. Higher Sensitivity ⇒ lower effective bar
    /// ⇒ the bot acts on weaker signals. Pure ⇒ testable.</summary>
    internal static bool PassesBar(double hot, double bar, double sens)
        => sens > 0.0 && hot >= bar / sens;

    /// <summary>Memoryless exit test for a HELD name: its thesis has decayed (Hot below ExitBar), momentum has
    /// flipped negative, or it prints overvalued past StopOvervaluation. Pure ⇒ testable.</summary>
    internal static bool ShouldExit(double hot, double mom, double overvaluation, double exitBar, double stopOvervaluation)
        => hot < exitBar || mom < 0.0 || overvaluation > stopOvervaluation;

    /// <summary>§P1 hold-horizon exit: a HARD exit (overvalued past the stop) fires immediately; the SOFT thesis-
    /// decay exit (Hot below ExitBar) only fires once the intended holding period has elapsed (heldSec ≥ holdSec).
    /// Unlike <see cref="ShouldExit"/> there is NO momentum knee-jerk — the bot HOLDS THROUGH DRAWDOWNS, so it can
    /// end underwater (real directional risk = win-rate ≪ 100%). Pure ⇒ testable.</summary>
    internal static bool ShouldExitHeld(double hot, double overvaluation, double exitBar, double stopOvervaluation,
        double heldSec, double holdSec)
        => overvaluation > stopOvervaluation || (hot < exitBar && heldSec >= holdSec);

    /// <summary>CK-safe bet size: min(RiskAppetite·seed, availCash − cashFloor), floored at 0 so a buy can never
    /// exceed available cash nor dip below the reserved floor. Pure ⇒ testable.</summary>
    internal static decimal DeployNotional(decimal riskNotional, decimal availCash, decimal cashFloorAmount)
    {
        decimal headroom = availCash - cashFloorAmount;
        if (headroom <= 0m) return 0m;
        decimal d = Math.Min(riskNotional, headroom);
        return d < 0m ? 0m : d;
    }

    /// <summary>§P2 conviction-scaled deploy fraction: a CONVEX power curve of conviction strength above the (already
    /// sensitivity-scaled) bar ⇒ MOST fires deploy a tiny fraction of the cash headroom, RARE exceptional convictions
    /// approach MaxDeploy. z = clamp(strength/convScale, 0, 1); frac = MaxDeploy·z^gamma. Pure ⇒ testable.</summary>
    internal static double ConvictionDeployFraction(double strength, double convScale, double maxDeploy, double gamma)
    {
        double z = Math.Clamp(strength / Math.Max(1e-9, convScale), 0.0, 1.0);
        return maxDeploy * Math.Pow(z, gamma);
    }

    /// <summary>§P3 open a short: the name prints OVERVALUED past ShortBar (price well above the bank estimate) and its
    /// momentum is NOT rising. Flat-only is enforced at the call site (Position.Quantity==0). Pure ⇒ testable.</summary>
    internal static bool ShouldOpenShort(double overvaluation, double mom, double shortBar)
        => overvaluation >= shortBar && mom <= 0.0;

    /// <summary>§P3 cover a held short: HYSTERESIS — the overvaluation has reverted below half the open bar (back toward
    /// fair value) OR momentum has turned up against the short. The 0.5·ShortBar band prevents open/cover thrash.
    /// Pure ⇒ testable.</summary>
    internal static bool ShouldCoverShort(double overvaluation, double mom, double shortBar)
        => overvaluation <= 0.5 * shortBar || mom > 0.0;

    /// <summary>§P3 short size (SHARES): a SMALL exposure = ShortRiskFraction·RiskAppetite·seedNotional / price, floored
    /// at 0. Shorts reserve collateral at fill (not cash at placement); this bounds EXPOSURE so the later cover buyback
    /// stays affordable from the cash pile. Pure ⇒ testable.</summary>
    internal static int ShortQty(decimal seedNotional, double riskAppetite, double shortRiskFraction, double price)
    {
        if (price <= 0.0) return 0;
        decimal notional = (decimal)(riskAppetite * shortRiskFraction) * seedNotional;
        if (notional <= 0m) return 0;
        int q = (int)Math.Floor((double)notional / price);
        return q > 0 ? q : 0;
    }

    /// <summary>§P4 signed two-way conviction: the SIGN chooses long(+)/short(−), the MAGNITUDE drives size. `gap` is
    /// the SIGNED fundamental (undervalued &gt;0 ⇒ long, overvalued &lt;0 ⇒ short) and REPLACES the one-way overvaluation
    /// veto of <see cref="Hot"/>. The SHARED carriers (sector sentiment, global) are NOT lean-flipped — all bots lean the
    /// same way on the shared signal ⇒ maximal cross-stock correlation. The OWN character terms (momentum, own sentiment)
    /// ARE lean-flipped (chaser +1 / fader −1). Fresh per-fire noise injects mistakes. Pure ⇒ testable.</summary>
    internal static double HotSigned(double gap, double sectorSent, double global, double mom, double ownSent, double noise,
        int lean, double wGap, double wSec, double wGlobal, double wMom, double wOwn, double wNoise)
    {
        double leanF = lean >= 0 ? 1.0 : -1.0;
        return wGap    * gap
             + wSec    * sectorSent      // shared carrier — NO lean (correlation)
             + wGlobal * global          // shared carrier — NO lean (correlation)
             + wMom    * leanF * mom      // own character — lean-flipped
             + wOwn    * leanF * ownSent  // own character — lean-flipped
             + wNoise  * noise;           // mistakes
    }

    /// <summary>§P4 the SIGNED fundamental gap in LOG space: ln(est/price) — symmetric under the ×3/÷3 band (±ln 3 at
    /// the edges), unlike the arithmetic (est−price)/est which is bounded +0.67 undervalued but −2.0 overvalued and
    /// would systematically over-size shorts (Fable review). Positive ⇒ undervalued ⇒ long. Pure ⇒ testable.</summary>
    internal static double LnGap(double est, double price)
        => est <= 0.0 || price <= 0.0 ? 0.0 : Math.Log(est / price);

    /// <summary>§P4 "satisfied with the result": the position's ENTRY gap has CLOSED — |gap| is now inside the band AND
    /// the gap at entry was OUTSIDE it on the position's side (a long entered undervalued, a short entered overvalued).
    /// Without the entry-gap condition a sentiment-led entry (gap≈0 at entry) is BORN satisfied and churns from its
    /// first review — the Fable-review degeneracy. Pure ⇒ testable.</summary>
    internal static bool GapSatisfied(double gap, double entryGap, double side, double satisfiedBand)
        => Math.Abs(gap) <= satisfiedBand && side * entryGap > satisfiedBand;

    /// <summary>§P4 conviction-led soft-horizon EXIT hazard for a HELD position (side +1 long / −1 short). Returns the
    /// per-review close PROBABILITY (caller compares vs a per-(bot,position,pass) hashed U01). Three ADDITIVE competing
    /// hazards so ANY can fire alone: (1) TIME — baseHazard × a convex ramp of heldSec/HoldSec (≈0 well before the
    /// horizon, =1 at it, grows past) ⇒ realized hold CENTERS on HoldSec, a soft indication not a wall; (2) CONVICTION
    /// TURNING AGAINST — alignment = side·hot; `against` grows as it decays below exitBar and especially FLIPS — its own
    /// per-review rate, so a broken thesis exits regardless of elapsed time (the strong trigger); (3) SATISFIED — the
    /// caller passes <see cref="GapSatisfied"/> (entry-record-aware: the ENTRY gap closed, not merely gap≈0). Conviction
    /// + gap are SHARED signals, so a whole sector's cohort distributes TOGETHER (correlation-positive), NOT a private-
    /// PnL fade. heldSec comes from the entry record (NOT Position.UpdatedAt, which moves on any write). A hard exit
    /// (crash / egregious overvaluation) is handled by the caller, bypassing this. Pure ⇒ testable.</summary>
    internal static double ExitHazard(double side, double hot, bool satisfied, double heldSec, double holdSec,
        double baseHazard, double exitBar, double flipGain, double satisfyGain, double timeExp)
    {
        double ratio     = holdSec <= 0.0 ? 1.0 : Math.Clamp(heldSec / holdSec, 0.0, 4.0);
        double timeTerm  = Math.Pow(ratio, Math.Max(0.1, timeExp));       // convex: small early, 1 at horizon, grows past
        double alignment = side * hot;                                    // >0 ⇒ the signal still agrees with the position
        double against   = Math.Max(0.0, exitBar - alignment);           // 0 while intact; grows as conviction decays/flips
        double rate = baseHazard * timeTerm + flipGain * against + (satisfied ? satisfyGain : 0.0);
        return Math.Clamp(rate, 0.0, 1.0);
    }

    /// <summary>§P5 basket selection: the top-K candidates at/above the bar, descending by Hot (Sid tie-break for
    /// replay determinism) — K=1 reproduces the single-best legacy pick. Pure ⇒ testable.</summary>
    internal static List<(int Sid, double Hot)> TopKAboveBar(List<(int Sid, double Hot)> candidates, double bar, int k)
    {
        var above = new List<(int Sid, double Hot)>();
        foreach (var c in candidates) if (c.Hot >= bar) above.Add(c);
        above.Sort((a, b) => a.Hot != b.Hot ? b.Hot.CompareTo(a.Hot) : a.Sid.CompareTo(b.Sid));
        if (above.Count > k) above.RemoveRange(k, above.Count - k);
        return above;
    }

    /// <summary>§mood fear-bid (Feature 3): a FEAR-ONLY (mood&lt;50), BUY-ONLY additive nudge to the conviction score —
    /// kFear·max(0,(50−laggedGlobalMood)/50). 0 when mood≥50 (never negative ⇒ can't force a short). Pure ⇒ testable.</summary>
    internal static double FearBid(double laggedGlobalMood, double kFear)
        => kFear * Math.Max(0.0, (50.0 - laggedGlobalMood) / 50.0);

    /// <summary>The current fear-bid to add to the conviction score (0 when the lever is off / no mood source).</summary>
    private double FearBidNow()
        => (_moodFearBid && _mood is not null) ? FearBid(_mood.LaggedGlobalMood(), _moodFearBidGain) : 0.0;

    private double CashFloorPctOf(int id)   => Dial(id, CashFloorSalt, _cashFloorBase, _cashFloorBase + CashFloorSpan);
    private double RiskAppetiteOf(int id)   => Math.Min(RiskAppetiteHardCap,
                                                   Dial(id, RiskSalt, _riskAppetiteBase, _riskAppetiteBase + RiskAppetiteSpan));
    private double ConvictionBarOf(int id)  => Dial(id, BarSalt, _convictionBarBase, _convictionBarBase + ConvictionBarSpan);
    private double SentimentSensOf(int id)  => Dial(id, SensSalt, SentimentSensLo, SentimentSensHi);
    private double CheckInMeanSecOf(int id) => Dial(id, CadenceSalt, _checkInMeanSecBase, _checkInMeanSecBase + CheckInMeanSpan);
    private double HoldSecOf(int id)        => Dial(id, HoldSalt, _holdMinSec, _holdMaxSec);   // §P1 per-bot hold horizon
}
