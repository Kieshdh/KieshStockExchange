# Linearity / trend diagnostic — quantifies the "price moves in a straight line" complaint. Per stock,
# over the window, on 1-min closes:
#   r2        : R^2 of a linear fit close~time. HIGH = a near-straight drift (bad). LOW = wavy/noisy (good).
#   net_move  : |close_end/close_start - 1|, the total directional displacement.
#   acf_lag5  : SIGNED return autocorr at lag 5 (multi-min trend persistence; >0 = trending, ~0 = no drift).
# Averaged across active stocks. A working contrarian feedback should LOWER r2 + net_move (+ pull acf_lag5
# toward 0) WITHOUT dragging ret_acf_lag1 more negative (check bounce_diag.py alongside).
import argparse, subprocess, sys
from collections import defaultdict

PG = "kieshstockexchange-postgres-1"

def linfit_r2(ys):
    n = len(ys)
    if n < 8: return None
    xs = list(range(n))
    mx = sum(xs)/n; my = sum(ys)/n
    sxx = sum((x-mx)**2 for x in xs)
    syy = sum((y-my)**2 for y in ys)
    sxy = sum((xs[i]-mx)*(ys[i]-my) for i in range(n))
    if sxx <= 0 or syy <= 0: return None
    return (sxy*sxy)/(sxx*syy)  # = r^2 of the linear fit

def acf(xs, lag):
    n = len(xs)
    if n <= lag+2: return None
    m = sum(xs)/n
    num = sum((xs[i]-m)*(xs[i-lag]-m) for i in range(lag, n))
    den = sum((x-m)**2 for x in xs)
    return num/den if den > 0 else None

def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--db", default="kse_soak")
    ap.add_argument("--window-min", type=float, default=40.0)
    ap.add_argument("--bucket-sec", type=int, default=60)
    args = ap.parse_args()

    sql = ('SELECT t."StockId", '
           f'floor(extract(epoch from t."Timestamp")/{args.bucket_sec}) AS b, '
           '(array_agg(t."Price" ORDER BY t."Timestamp" DESC))[1] AS close '
           'FROM "Transactions" t '
           f'WHERE extract(epoch from t."Timestamp") >= extract(epoch from now()) - {args.window_min*60:.0f} '
           'GROUP BY t."StockId", b ORDER BY t."StockId", b;')
    out = subprocess.run(["docker","exec","-i",PG,"psql","-U","kse","-d",args.db,"--csv","-c",sql],
                         capture_output=True, text=True)
    if out.returncode != 0: sys.exit(f"psql failed: {out.stderr.strip()}")

    closes = defaultdict(list)
    for ln in out.stdout.splitlines()[1:]:
        p = ln.split(",")
        if len(p) < 3 or not p[2]: continue
        closes[int(p[0])].append(float(p[2]))

    r2s, moves, acf5s = [], [], []
    for sid, cs in closes.items():
        if len(cs) < 8: continue
        r2 = linfit_r2(cs)
        if r2 is not None: r2s.append(r2)
        if cs[0] > 0: moves.append(abs(cs[-1]/cs[0] - 1.0))
        rets = [(cs[i]-cs[i-1])/cs[i-1] for i in range(1, len(cs)) if cs[i-1] > 0]
        a5 = acf(rets, 5)
        if a5 is not None: acf5s.append(a5)

    n = len(r2s)
    print(f"db={args.db} window={args.window_min:.0f}m stocks={n}")
    if n:
        print(f"  linear_fit_r2 : {sum(r2s)/n:.3f}   (HIGH=straight-line drift/bad, LOW=wavy/good)")
        print(f"  net_move      : {sum(moves)/len(moves)*100:.2f}%  (avg |total displacement|)")
        print(f"  ret_acf_lag5  : {sum(acf5s)/len(acf5s):+.3f}  (multi-min trend persistence; ~0 = no drift)")

if __name__ == "__main__":
    main()
