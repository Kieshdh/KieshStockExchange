#!/usr/bin/env python3
"""Harvest BotPhase telemetry from a soak log for A/B perf comparison.

The bot loop emits (when Bots:PhaseTimingSeconds>0):
  BotPhase [N ticks, cap C]: T ms/tick = check .. + collect .. + batch .. + adv ..
    + arb .. + recon .. + maint .. (ms); P orders + A adv/tick; K commits (K/sec, R round-trips/order)

For staggering / gate-split A/B the headline axis is the EQUILIBRIUM bot cap (the scaler
self-levels ms to its setpoint, so a per-tick load cut shows up as a HIGHER cap at the same
ms, not lower ms). We report the tail-window mean cap + orders/tick + adv/tick + ms/tick +
arb-ms/tick + commits/sec + round-trips/order, and scan for conservation/health signals so a
win is only banked when clean. Numeric fields render invariant-culture (Serilog) => '.'-decimal.
"""
import re, sys, glob, statistics as st

# Groups: 1 ticks, 2 cap, 3 ms(total), 4 arb-ms, 5 orders, 6 adv, then an OPTIONAL commits
# suffix 7 commits, 8 /sec, 9 round-trips/order (None on logs predating that suffix).
PHASE = re.compile(
    r"BotPhase \[(\d+) ticks, cap (\w+)\]: ([\d.]+)ms/tick.*?arb ([\d.]+).*?"
    r"; ([\d.]+) orders \+ ([\d.]+) adv/tick"
    r"(?:; ([\d.]+) commits \(([\d.]+)/sec, ([\d.]+) round-trips/order\))?")
# CK gate: match on format-independent MESSAGE CONTENT (survives every Serilog template/formatter).
# "Money/Shares probe" = ConservationProbe violations; "exceeds tolerance" = ReservationAuditor
# over-tolerance WARN (not the benign within-tolerance Debug clamp); CK_*/check constraint = DB.
SIGNALS = ("Money probe", "Shares probe", "exceeds tolerance", "CK_Funds", "CK_Positions",
           "check constraint", "Short-close collateral shortfall")

def harvest(path, tail_frac=0.5):
    caps, orders, advs, ms, arb, cps, rto = [], [], [], [], [], [], []
    sig = {s: 0 for s in SIGNALS}
    with open(path, encoding="utf-8", errors="ignore") as f:
        for line in f:
            m = PHASE.search(line)
            if m:
                cap = m.group(2)
                if cap.isdigit():  # skip 'all'-cap lines; keep every list length-aligned
                    caps.append(int(cap))
                    ms.append(float(m.group(3)))
                    arb.append(float(m.group(4)))
                    orders.append(float(m.group(5)))
                    advs.append(float(m.group(6)))
                    if m.group(8) is not None:  # commits suffix (all-or-nothing per log)
                        cps.append(float(m.group(8)))
                        rto.append(float(m.group(9)))
            for s in SIGNALS:
                if s in line:
                    sig[s] += 1
    n = len(caps)
    if n == 0:
        return None
    def tail(xs, r=1):  # tail-window mean over each list's OWN length; None when window empty
        w = xs[max(1, int(len(xs) * (1 - tail_frac))):] if xs else []
        return round(st.mean(w), r) if w else None
    return dict(
        samples=n, tail_n=n - max(1, int(n * (1 - tail_frac))),
        cap=tail(caps), cap_max=max(caps), cap_last=caps[-1],
        orders=tail(orders), adv=tail(advs, 2), ms=tail(ms), arb=tail(arb, 2),
        cps=tail(cps, 1), rto=tail(rto, 3),
        sig={s: c for s, c in sig.items() if c})

def main():
    for path in sys.argv[1:] or sorted(glob.glob("logs/soakP-*.log")):
        r = harvest(path)
        name = path.split("/")[-1].split("\\")[-1]
        if not r:
            print(f"{name}: no BotPhase samples"); continue
        def na(x): return x if x is not None else "n/a"
        print(f"{name}")
        print(f"  samples={r['samples']} (tail {r['tail_n']}) | "
              f"cap tail-mean={na(r['cap'])} max={r['cap_max']} last={r['cap_last']} | "
              f"orders/tick={na(r['orders'])} adv/tick={na(r['adv'])} | "
              f"ms/tick={na(r['ms'])} arb-ms/tick={na(r['arb'])}")
        print(f"  commits/sec={na(r['cps'])} round-trips/order={na(r['rto'])}")
        print(f"  signals: {r['sig'] or 'CLEAN'}")

if __name__ == "__main__":
    main()
