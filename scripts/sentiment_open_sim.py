# Standalone simulation of BotSentimentService's OU rings (Reset + Tick) to validate the
# neutral-open fix WITHOUT a server soak. Replicates the exact math:
#   ring_new = a*ring + sigma*sqrt(1-a^2)*noise,  a = exp(-dt/tau),  noise = U(-1,1)*sqrt3
#   combined(stock) = sum(per-stock rings) + sum(global rings)   [shocks off]
# Compares OLD reset (rings seeded at sigma*noise -> nonzero, biased open) vs NEW (rings = 0).
import math, random

GLOBAL_TAU = [600, 3600, 21600];      GLOBAL_SIG = [0.10, 0.08, 0.06]
PS_TAU = [20, 90, 360, 1800, 10800];  PS_SIG = [0.25, 0.25, 0.20, 0.12, 0.08]
SQRT3 = math.sqrt(3.0)
N_STOCKS = 50
DT = 1.0

def unit(rng): return (rng.random() * 2.0 - 1.0) * SQRT3

def step(ring, tau, sig, rng):
    for k in range(len(ring)):
        a = math.exp(-DT / tau[k])
        ring[k] = a * ring[k] + sig[k] * math.sqrt(1 - a * a) * unit(rng)

def run(seed, neutral, minutes=20):
    rng = random.Random(seed)
    g = [0.0] * 3 if neutral else [GLOBAL_SIG[k] * unit(rng) for k in range(3)]
    stocks = [[0.0] * 5 if neutral else [PS_SIG[k] * unit(rng) for k in range(5)] for _ in range(N_STOCKS)]
    out = []  # (t_sec, globalSum, combined_mean_across_stocks, combined_std_across_stocks)
    for t in range(minutes * 60 + 1):
        gsum = sum(g)
        comb = [gsum + sum(s) for s in stocks]
        m = sum(comb) / len(comb)
        sd = (sum((c - m) ** 2 for c in comb) / len(comb)) ** 0.5
        if t in (0, 30, 60, 300, 600, 1200): out.append((t, gsum, m, sd))
        step(g, GLOBAL_TAU, GLOBAL_SIG, rng)
        for s in stocks: step(s, PS_TAU, PS_SIG, rng)
    return out

def avg_globalsum_at(neutral, seeds=2000, minutes=20):
    # mean of globalSum across many seeds -> shows whether the MODEL is inherently biased.
    # globalSum depends only on the 3 global rings (not stocks), so simulate those alone (fast).
    samples = (0, 30, 60, 300, 600, 1200)
    acc = {t: 0.0 for t in samples}
    for sd in range(seeds):
        rng = random.Random(sd)
        g = [0.0] * 3 if neutral else [GLOBAL_SIG[k] * unit(rng) for k in range(3)]
        for t in range(minutes * 60 + 1):
            if t in acc: acc[t] += sum(g)
            step(g, GLOBAL_TAU, GLOBAL_SIG, rng)
    return [(t, acc[t] / seeds) for t in samples]

print("=== single run (seed 43), NEW neutral open ===")
print("  t(s)   globalSum   combined_mean   combined_std(dispersion)")
for t, g, m, sd in run(43, neutral=True):
    print(f"  {t:5d}   {g:+8.4f}    {m:+8.4f}       {sd:7.4f}")

print("\n=== single run (seed 43), OLD biased open (for contrast) ===")
print("  t(s)   globalSum   combined_mean   combined_std(dispersion)")
for t, g, m, sd in run(43, neutral=False):
    print(f"  {t:5d}   {g:+8.4f}    {m:+8.4f}       {sd:7.4f}")

print("\n=== mean globalSum across 400 seeds (is there a SYSTEMATIC bias?) ===")
print("  t(s)    NEW(neutral)     OLD(biased)")
new = dict(avg_globalsum_at(True)); old = dict(avg_globalsum_at(False))
for t in (0, 30, 60, 300, 600, 1200):
    print(f"  {t:5d}    {new[t]:+10.5f}     {old[t]:+10.5f}")
