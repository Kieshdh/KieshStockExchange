# Bid-ask bounce diagnostic: compare lag-1 autocorr of 1-min returns built on the CLOSE (last trade,
# which alternates bid/ask -> bounce) vs VWAP (minute volume-weighted avg, which averages the bounce out).
# If close-AC1 is strongly negative but VWAP-AC1 ~ 0, the negative return autocorr is microstructure
# (bid-ask bounce), not genuine mean-reversion. Reads Transactions straight from a soak DB.
import argparse, subprocess, sys
from collections import defaultdict

PG = "kieshstockexchange-postgres-1"

def autocorr1(xs):
    n = len(xs)
    if n < 8:
        return None
    m = sum(xs) / n
    num = sum((xs[i] - m) * (xs[i-1] - m) for i in range(1, n))
    den = sum((x - m) ** 2 for x in xs)
    return num / den if den > 0 else None

def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--db", default="kse_soak")
    ap.add_argument("--window-min", type=float, default=40.0)
    ap.add_argument("--bucket-sec", type=int, default=60)
    args = ap.parse_args()

    sql = (
        'SELECT t."StockId", '
        f'floor(extract(epoch from t."Timestamp")/{args.bucket_sec}) AS b, '
        '(array_agg(t."Price" ORDER BY t."Timestamp" DESC))[1] AS close, '
        'sum(t."Price"*t."Quantity")/NULLIF(sum(t."Quantity"),0) AS vwap '
        'FROM "Transactions" t '
        f'WHERE extract(epoch from t."Timestamp") >= extract(epoch from now()) - {args.window_min*60:.0f} '
        'GROUP BY t."StockId", b ORDER BY t."StockId", b;'
    )
    out = subprocess.run(["docker", "exec", "-i", PG, "psql", "-U", "kse", "-d", args.db, "--csv", "-c", sql],
                         capture_output=True, text=True)
    if out.returncode != 0:
        sys.exit(f"psql failed: {out.stderr.strip()}")

    closes, vwaps = defaultdict(list), defaultdict(list)
    for ln in out.stdout.splitlines()[1:]:
        p = ln.split(",")
        if len(p) < 4 or not p[2] or not p[3]:
            continue
        closes[int(p[0])].append(float(p[2]))
        vwaps[int(p[0])].append(float(p[3]))

    def rets(series):
        return [(series[i] - series[i-1]) / series[i-1] for i in range(1, len(series)) if series[i-1] > 0]

    c_acs, v_acs, c_absacs = [], [], []
    for sid in closes:
        cr = rets(closes[sid])
        ca = autocorr1(cr)
        va = autocorr1(rets(vwaps[sid]))
        caa = autocorr1([abs(x) for x in cr])  # §exogenous-information: clustering on the headline population
        if ca is not None:
            c_acs.append(ca)
        if va is not None:
            v_acs.append(va)
        if caa is not None:
            c_absacs.append(caa)

    print(f"db={args.db} window={args.window_min:.0f}m bucket={args.bucket_sec}s stocks={len(c_acs)}")
    if c_acs:
        print(f"  ret_acf_lag1     CLOSE (last trade, bounces): {sum(c_acs)/len(c_acs):+.3f}")
    if v_acs:
        print(f"  ret_acf_lag1     VWAP  (bounce averaged out):  {sum(v_acs)/len(v_acs):+.3f}")
    if c_acs and v_acs:
        diff = sum(v_acs)/len(v_acs) - sum(c_acs)/len(c_acs)
        print(f"  => VWAP - CLOSE = {diff:+.3f}  (large positive => the negative AC1 is bid-ask bounce)")
    if c_absacs:
        # Volatility-clustering guard on the SAME 50-stock CLOSE population as the headline ret_acf, so the
        # WIN-gate's absret_acf_lag1 >= 0.10 sub-gate is measured where the headline is, not only on the 16-stock scorer.
        print(f"  absret_acf_lag1  CLOSE (clustering guard):    {sum(c_absacs)/len(c_absacs):+.3f}  (want >= 0.10)")

if __name__ == "__main__":
    main()
