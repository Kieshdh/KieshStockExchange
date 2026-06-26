#!/usr/bin/env python3
"""Cross-stock co-movement + fat-tail diagnostic on soak candle CSVs.

Measures the realism axes the 1-min ret_acf scorer ignores:
  - mean pairwise return correlation across stocks (real equity ~0.2-0.5; 0 = "independent universes")
  - common-factor R2: how much each stock's return is explained by the equal-weight market return
  - excess kurtosis of 1-min returns (real ~3-8 excess; 0 = Gaussian, our prior gap was ~ -? / thin)

Skips '#' comment lines. One currency only (default USD) so stock-vs-stock isn't conflated with
the ~1.0 USD/EUR same-stock correlation. Pure-Python (no numpy/scipy dependency). ASCII output.

Usage: python scripts/cross_stock_diag.py --csv data/soaks/candles-XXX.csv [--currency USD]
"""
import argparse, csv, math, statistics


def load(path, currency):
    rows = []
    with open(path, newline="", encoding="utf-8") as fh:
        lines = [ln for ln in fh if not ln.lstrip().startswith("#")]
    rdr = csv.DictReader(lines)
    cols = {c.lower(): c for c in (rdr.fieldnames or [])}
    def pick(*names):
        for n in names:
            if n in cols:
                return cols[n]
        return None
    c_stock = pick("stockid", "stock_id", "symbol", "sid")
    c_ccy   = pick("currency", "currencytype", "ccy")
    c_time  = pick("opentime", "open_time", "bucket_epoch", "bucket", "epoch", "time", "timestamp", "ts")
    c_close = pick("close", "closeprice", "c")
    if not (c_stock and c_time and c_close):
        raise SystemExit("could not find stock/time/close columns in: %s" % (rdr.fieldnames,))
    for r in rdr:
        if c_ccy and currency and str(r[c_ccy]).strip().upper() not in (currency.upper(), ""):
            continue
        try:
            rows.append((str(r[c_stock]).strip(), str(r[c_time]).strip(), float(r[c_close])))
        except (ValueError, KeyError):
            continue
    return rows, (c_stock, c_ccy, c_time, c_close)


def log_returns(series):
    out = []
    for i in range(1, len(series)):
        a, b = series[i - 1], series[i]
        if a > 0 and b > 0:
            out.append(math.log(b / a))
        else:
            out.append(0.0)
    return out


def pearson(x, y):
    n = len(x)
    if n < 3:
        return None
    mx, my = sum(x) / n, sum(y) / n
    sxx = sum((a - mx) ** 2 for a in x)
    syy = sum((b - my) ** 2 for b in y)
    sxy = sum((a - mx) * (b - my) for a, b in zip(x, y))
    if sxx <= 0 or syy <= 0:
        return None
    return sxy / math.sqrt(sxx * syy)


def excess_kurt(x):
    n = len(x)
    if n < 4:
        return None
    m = sum(x) / n
    var = sum((a - m) ** 2 for a in x) / n
    if var <= 0:
        return None
    m4 = sum((a - m) ** 4 for a in x) / n
    return m4 / (var ** 2) - 3.0


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--csv", required=True)
    ap.add_argument("--currency", default="USD")
    args = ap.parse_args()

    rows, used = load(args.csv, args.currency)
    # group close series per stock, ordered by time
    by_stock = {}
    for sid, t, close in rows:
        by_stock.setdefault(sid, []).append((t, close))
    closes, all_times = {}, set()
    for sid, pts in by_stock.items():
        pts.sort(key=lambda p: p[0])
        if len(pts) >= 10:
            closes[sid] = dict(pts)
            all_times.update(t for t, _ in pts)
    times = sorted(all_times)

    # return series aligned on the common timeline (None where missing)
    ret = {}
    for sid, cmap in closes.items():
        seq = [cmap.get(t) for t in times]
        present = [(i, v) for i, v in enumerate(seq) if v is not None]
        rs = {}
        for k in range(1, len(present)):
            (i0, v0), (i1, v1) = present[k - 1], present[k]
            if i1 == i0 + 1 and v0 > 0 and v1 > 0:
                rs[i1] = math.log(v1 / v0)
        ret[sid] = rs

    sids = sorted(ret)
    # mean pairwise correlation on overlapping timestamps
    corrs = []
    for a in range(len(sids)):
        for b in range(a + 1, len(sids)):
            ra, rb = ret[sids[a]], ret[sids[b]]
            common = sorted(set(ra) & set(rb))
            if len(common) >= 10:
                c = pearson([ra[i] for i in common], [rb[i] for i in common])
                if c is not None:
                    corrs.append(c)
    # common factor = equal-weight mean return per timestamp; per-stock corr vs factor
    factor = {}
    for i in range(len(times)):
        vals = [ret[s][i] for s in sids if i in ret[s]]
        if len(vals) >= 3:
            factor[i] = sum(vals) / len(vals)
    loadings = []
    for s in sids:
        common = sorted(set(ret[s]) & set(factor))
        if len(common) >= 10:
            c = pearson([ret[s][i] for i in common], [factor[i] for i in common])
            if c is not None:
                loadings.append(c * c)  # R^2
    # kurtosis: per-stock excess kurtosis of returns, then summarize
    kurts = [k for s in sids if (k := excess_kurt(list(ret[s].values()))) is not None]

    print("=== cross-stock diagnostic ===")
    print("csv         :", args.csv.split("/")[-1])
    print("columns used:", used)
    print("currency    :", args.currency)
    print("stocks      :", len(sids), " timeline buckets:", len(times))
    print()
    if corrs:
        corrs.sort()
        print("PAIRWISE RETURN CORRELATION (cross-stock co-movement):")
        print("  pairs=%d  mean=%+.3f  median=%+.3f  p10=%+.3f  p90=%+.3f"
              % (len(corrs), statistics.mean(corrs), statistics.median(corrs),
                 corrs[len(corrs)//10], corrs[len(corrs)*9//10]))
        print("  -> real equity ~+0.20..+0.50; ~0 = independent universes (the gap)")
    if loadings:
        print("COMMON-FACTOR R2 (variance explained by equal-weight market return):")
        print("  stocks=%d  mean=%.3f  median=%.3f" % (len(loadings), statistics.mean(loadings), statistics.median(loadings)))
    if kurts:
        kurts.sort()
        print("EXCESS KURTOSIS of 1-min returns (fat tails):")
        print("  stocks=%d  mean=%+.2f  median=%+.2f  p90=%+.2f"
              % (len(kurts), statistics.mean(kurts), statistics.median(kurts), kurts[len(kurts)*9//10]))
        print("  -> real equity excess kurt ~ +3..+8; ~0 = Gaussian/thin tails")


if __name__ == "__main__":
    main()
