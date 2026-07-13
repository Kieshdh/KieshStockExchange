"""Volume-clustering diagnostics on a soak candle CSV.

Reports the two headline stats from docs/bot-variable-volume.md #7:
  1. per-minute volume autocorrelation (lag 1..10)  -- real: positive, slow decay
  2. volume-volatility correlation corr(vol_t, |ret_t|) -- real: clearly positive
plus CV of per-minute total volume and per-stock medians.
"""
import sys, csv, math
from collections import defaultdict

def autocorr(x, lag):
    n = len(x)
    if n <= lag + 2: return float('nan')
    m = sum(x) / n
    v = sum((a - m) ** 2 for a in x)
    if v == 0: return float('nan')
    return sum((x[i] - m) * (x[i + lag] - m) for i in range(n - lag)) / v

def corr(a, b):
    n = len(a)
    if n < 3: return float('nan')
    ma, mb = sum(a)/n, sum(b)/n
    va = math.sqrt(sum((x-ma)**2 for x in a)); vb = math.sqrt(sum((x-mb)**2 for x in b))
    if va == 0 or vb == 0: return float('nan')
    return sum((a[i]-ma)*(b[i]-mb) for i in range(n)) / (va*vb)

def median(xs):
    xs = sorted(x for x in xs if not math.isnan(x))
    if not xs: return float('nan')
    n = len(xs)
    return xs[n//2] if n % 2 else (xs[n//2-1]+xs[n//2])/2

path = sys.argv[1]
rows = []
with open(path) as f:
    for line in f:
        if line.startswith('#') or line.startswith('stock_id'): continue
        p = line.strip().split(',')
        if len(p) < 7: continue
        rows.append((int(p[0]), int(p[1]), float(p[5]), float(p[6])))  # sid, bucket, close, volume

# trim first/last 10 min (open transient / partial buckets)
buckets = sorted({r[1] for r in rows})
lo, hi = buckets[10], buckets[-2]
rows = [r for r in rows if lo <= r[1] <= hi]

# 1) total per-minute volume series (fill missing buckets with 0)
tot = defaultdict(float)
for sid, b, c, v in rows: tot[b] += v
allb = list(range(lo, hi + 60, 60))
series = [tot.get(b, 0.0) for b in allb]
m = sum(series)/len(series)
sd = math.sqrt(sum((x-m)**2 for x in series)/len(series))
print(f"total per-min volume: n={len(series)} mean={m:,.0f} sd={sd:,.0f} CV={sd/m:.3f}")
print("  total-vol autocorr:", " ".join(f"L{l}={autocorr(series,l):+.3f}" for l in (1,2,3,5,10,20,30)))

# 2) per-stock volume autocorr + vol-|ret| corr
ac1, ac5, vv = [], [], []
per = defaultdict(dict)
for sid, b, c, v in rows: per[sid][b] = (c, v)
for sid, d in per.items():
    bs = sorted(d)
    if len(bs) < 20: continue
    vols = [d[b][1] for b in bs]
    rets = []
    for i in range(1, len(bs)):
        c0, c1 = d[bs[i-1]][0], d[bs[i]][0]
        rets.append(abs(math.log(c1/c0)) if c0 > 0 and c1 > 0 else 0.0)
    ac1.append(autocorr(vols, 1)); ac5.append(autocorr(vols, 5))
    vv.append(corr(vols[1:], rets))
print(f"per-stock (n={len(ac1)}): vol autocorr L1 median={median(ac1):+.3f}  L5 median={median(ac5):+.3f}")
print(f"per-stock vol~|ret| corr: median={median(vv):+.3f}  p10={sorted(vv)[max(0,int(len(vv)*.1))]:+.3f}  p90={sorted(vv)[min(len(vv)-1,int(len(vv)*.9))]:+.3f}")
