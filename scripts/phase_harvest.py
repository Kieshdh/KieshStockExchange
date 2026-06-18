#!/usr/bin/env python3
"""Harvest BotPhase telemetry from a soak log for A/B perf comparison.

The bot loop emits (when Bots:PhaseTimingSeconds>0):
  BotPhase [N ticks, cap C]: T ms/tick = check .. + batch .. + adv .. (ms); P orders + A adv/tick

For staggering / gate-split A/B the headline axis is the EQUILIBRIUM bot cap (the scaler
self-levels ms to its setpoint, so a per-tick load cut shows up as a HIGHER cap at the same
ms, not lower ms). We report the tail-window mean cap + orders/tick + adv/tick + ms/tick, and
scan for conservation/health signals so a win is only banked when clean.
"""
import re, sys, glob, statistics as st

PHASE = re.compile(
    r"BotPhase \[(\d+) ticks, cap (\w+)\]: ([\d.]+)ms/tick.*?; ([\d.]+) orders \+ ([\d.]+) adv/tick")
SIGNALS = ("Conservation", "CK_Funds", "CK_Positions", "check constraint", "[ERR]",
           "Short-close collateral shortfall", "ReservationAuditor")

def harvest(path, tail_frac=0.5):
    caps, orders, advs, ms = [], [], [], []
    sig = {s: 0 for s in SIGNALS}
    with open(path, encoding="utf-8", errors="ignore") as f:
        for line in f:
            m = PHASE.search(line)
            if m:
                cap = m.group(2)
                if cap.isdigit():
                    caps.append(int(cap))
                    ms.append(float(m.group(3)))
                    orders.append(float(m.group(4)))
                    advs.append(float(m.group(5)))
            for s in SIGNALS:
                if s in line:
                    sig[s] += 1
    n = len(caps)
    if n == 0:
        return None
    k = max(1, int(n * (1 - tail_frac)))  # tail window = last tail_frac of samples
    tail = slice(k, n)
    def mean(xs): return round(st.mean(xs), 1) if xs else 0
    return dict(
        samples=n, tail_n=n - k,
        cap=mean(caps[tail]), cap_max=max(caps), cap_last=caps[-1],
        orders=mean(orders[tail]), adv=round(mean(advs[tail]), 2),
        ms=mean(ms[tail]), sig={s: c for s, c in sig.items() if c})

def main():
    for path in sys.argv[1:] or sorted(glob.glob("logs/soakP-*.log")):
        r = harvest(path)
        name = path.split("/")[-1].split("\\")[-1]
        if not r:
            print(f"{name}: no BotPhase samples"); continue
        print(f"{name}")
        print(f"  samples={r['samples']} (tail {r['tail_n']}) | "
              f"cap tail-mean={r['cap']} max={r['cap_max']} last={r['cap_last']} | "
              f"orders/tick={r['orders']} adv/tick={r['adv']} | ms/tick={r['ms']}")
        print(f"  signals: {r['sig'] or 'CLEAN'}")

if __name__ == "__main__":
    main()
