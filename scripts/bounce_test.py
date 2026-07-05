# Council bounce-vs-behavior test: recompute 1-min ret_acf(lag-1) on the SAME trades sampled 3 ways
# (last-trade close / bounce-free mid close / VWAP). If mid/VWAP >> last-trade, the -0.35 is bid-ask
# bounce (a sampling artifact); if all three stay ~-0.35, it's behavioral. Input CSV: stock_id,minute,
# last_close,mid_close,vwap (per-stock per-minute, from the Transactions table).
import csv, sys, math
from collections import defaultdict

def acf1(xs):
    n = len(xs)
    if n < 3: return None
    m = sum(xs) / n
    den = sum((x - m) ** 2 for x in xs)
    if den <= 0: return None
    num = sum((xs[i] - m) * (xs[i + 1] - m) for i in range(n - 1))
    return num / den

def logrets(prices):
    out = []
    for i in range(1, len(prices)):
        a, b = prices[i - 1], prices[i]
        if a and b and a > 0 and b > 0:
            out.append(math.log(b / a))
    return out

def median(vals):
    vals = sorted(v for v in vals if v is not None)
    if not vals: return None
    n = len(vals)
    return vals[n // 2] if n % 2 else (vals[n // 2 - 1] + vals[n // 2]) / 2

for path in sys.argv[1:]:
    bystock = defaultdict(list)
    with open(path) as f:
        for row in csv.DictReader(f):
            try:
                bystock[row['stock_id']].append((
                    int(row['minute']),
                    float(row['last_close']) if row['last_close'] else None,
                    float(row['mid_close']) if row['mid_close'] else None,
                    float(row['vwap']) if row['vwap'] else None))
            except Exception:
                pass
    accs = {'last': [], 'mid': [], 'vwap': []}
    for rows in bystock.values():
        rows.sort()
        cols = {'last': [x[1] for x in rows], 'mid': [x[2] for x in rows], 'vwap': [x[3] for x in rows]}
        for key, series in cols.items():
            a = acf1(logrets(series))
            if a is not None:
                accs[key].append(a)
    print(f"== {path}  ({len(bystock)} stocks) ==")
    for key in ('last', 'mid', 'vwap'):
        med = median(accs[key])
        print(f"  ret_acf({key:4}) median = {med:+.3f}   (n={len(accs[key])})")
