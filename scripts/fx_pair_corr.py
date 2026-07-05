# Arbitrage effect: for each CROSS-LISTED stock, correlate its USD-listing vs EUR-listing 1-min returns.
# Arb pins USD ~= EUR*FX, so the two listings should co-move (corr ~1). Also report the log(USD/EUR) std
# (level-coupling / parity tightness). Input CSV: stock_id,ccy,minute,price (per-stock per-ccy per-minute).
import csv, sys, math
from collections import defaultdict

def pearson(x, y):
    n = len(x)
    if n < 3: return None
    mx, my = sum(x)/n, sum(y)/n
    sxy = sum((x[i]-mx)*(y[i]-my) for i in range(n))
    sxx = sum((v-mx)**2 for v in x); syy = sum((v-my)**2 for v in y)
    if sxx <= 0 or syy <= 0: return None
    return sxy/math.sqrt(sxx*syy)

def median(vals):
    vals = sorted(v for v in vals if v is not None)
    if not vals: return None
    n = len(vals); return vals[n//2] if n % 2 else (vals[n//2-1]+vals[n//2])/2

path = sys.argv[1]
bystock = defaultdict(lambda: {'USD': {}, 'EUR': {}})
with open(path) as f:
    for row in csv.DictReader(f):
        try:
            p = float(row['price'])
            if row['ccy'] in ('USD', 'EUR') and p > 0:
                bystock[row['stock_id']][row['ccy']][int(row['minute'])] = p
        except Exception:
            pass

rcorrs, lcorrs, lrstds, rows = [], [], [], []
for sid, d in sorted(bystock.items(), key=lambda kv: int(kv[0])):
    usd, eur = d['USD'], d['EUR']
    common = sorted(set(usd) & set(eur))            # minutes where BOTH books traded
    ru, re, lr, lu, le = [], [], [], [], []
    for i in range(len(common)):
        m = common[i]
        lr.append(math.log(usd[m]/eur[m]))          # log(USD/EUR) ~= log(FX) if arb pins it
        lu.append(math.log(usd[m])); le.append(math.log(eur[m]))   # log-LEVELS
        if i > 0:
            m0 = common[i-1]
            ru.append(math.log(usd[m]/usd[m0])); re.append(math.log(eur[m]/eur[m0]))
    rc = pearson(ru, re)          # RETURN correlation
    lc = pearson(lu, le)          # LEVEL correlation (do the two price lines overlay?)
    lrstd = (sum((v-sum(lr)/len(lr))**2 for v in lr)/len(lr))**0.5 if len(lr) > 1 else None
    if rc is not None:
        rcorrs.append(rc); lcorrs.append(lc); lrstds.append(lrstd); rows.append((sid, rc, lc, lrstd, len(ru)))

print(f"cross-listed stocks measured: {len(rcorrs)} (of {len(bystock)})")
if rcorrs:
    print(f"  USD-vs-EUR LEVEL corr (log-price overlay):  median={median(lcorrs):+.3f}  mean={sum(lcorrs)/len(lcorrs):+.3f}")
    print(f"  USD-vs-EUR 1-min RETURN corr:                median={median(rcorrs):+.3f}  mean={sum(rcorrs)/len(rcorrs):+.3f}")
    print(f"  log(USD/EUR) std (parity band; ~FX-drift+arb-error): median={median(lrstds):.4f}")
    print("  per-stock:")
    for sid, rc, lc, lr, n in rows:
        print(f"    stock {sid:>3}: level_corr={lc:+.3f}  return_corr={rc:+.3f}  band_std={lr:.4f}  common_min={n+1}")
