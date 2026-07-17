# Drop the first N minutes (warm-up / price-discovery) from a soak candle CSV and re-base drift to the
# first POST-warmup close (so "drift" is the trend after the market found its level, not the seed jump).
# Writes <csv>.trim<N>.csv and prints the re-based cross-stock drift. Usage: py trim_warmup.py <csv> [N]
import csv, sys, statistics as st
path = sys.argv[1]; skip = int(sys.argv[2]) if len(sys.argv) > 2 else 5
rows = []
with open(path, newline='') as fh:
    for ln in fh:
        if ln.lstrip().startswith('#'): continue
        rows.append(ln)
hdr = rows[0].rstrip('\n').split(',')
ix = {c: i for i, c in enumerate(hdr)}
sid_c, ep_c, cl_c = ix['stock_id'], ix['bucket_epoch'], ix['close']
data = [r.rstrip('\n').split(',') for r in rows[1:]]
eps = [int(r[ep_c]) for r in data]
cut = min(eps) + skip * 60
kept = [r for r in data if int(r[ep_c]) >= cut]
out = path.replace('.csv', f'.trim{skip}.csv')
with open(out, 'w', newline='') as fh:
    fh.write(','.join(hdr) + '\n')
    for r in kept: fh.write(','.join(r) + '\n')
# Re-based drift: per stock, last close / first post-warmup close - 1.
bysid = {}
for r in kept:
    bysid.setdefault(r[sid_c], []).append((int(r[ep_c]), float(r[cl_c])))
drifts = []
for sid, seq in bysid.items():
    seq.sort()
    if len(seq) >= 2 and seq[0][1] > 0:
        drifts.append((seq[-1][1] / seq[0][1] - 1) * 100)
print(f"wrote {out}  ({len(kept)} candles, skipped first {skip}m)")
if drifts:
    drifts.sort()
    print(f"RE-BASED drift (vs post-warmup price, {len(drifts)} stocks): "
          f"mean={st.mean(drifts):+.2f}%  median={st.median(drifts):+.2f}%  "
          f"min={drifts[0]:+.2f}%  max={drifts[-1]:+.2f}%")
