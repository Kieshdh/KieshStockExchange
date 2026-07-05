#!/usr/bin/env python3
"""Cross-stock co-movement + fat-tail diagnostic on soak candle CSVs.

Measures the realism axes the 1-min ret_acf scorer ignores:
  - mean pairwise return correlation across stocks at multiple horizons (real equity ~0.2-0.5; 0 =
    "independent universes"). A SLOW shared market factor can show ~0 at the 1-min horizon yet positive
    at 5-min, so we report both — that distinguishes "channel works but slow" from "channel inert".
  - common-factor R2: how much each stock's return is explained by the equal-weight market return
  - excess kurtosis of 1-min returns (real ~3-8 excess; 0 = Gaussian/thin)

Skips '#' comment lines. One currency only (default USD) so stock-vs-stock isn't conflated with the
~1.0 USD/EUR same-stock correlation (no-op if there is no currency column). Pure-Python. ASCII output.

Usage: python scripts/cross_stock_diag.py --csv data/soaks/candles-XXX.csv [--currency USD] [--horizons 1,5]
"""
import argparse, csv, math, statistics


def load(path, currency):
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
    rows = []
    for r in rdr:
        if c_ccy and currency and str(r[c_ccy]).strip().upper() not in (currency.upper(), ""):
            continue
        try:
            rows.append((str(r[c_stock]).strip(), str(r[c_time]).strip(), float(r[c_close])))
        except (ValueError, KeyError):
            continue
    return rows, (c_stock, c_ccy, c_time, c_close)


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


def returns_at(closes, times, h):
    """h-bucket log returns per stock, keyed by timeline index (gaps respected via the aligned series)."""
    ret = {}
    for sid, cmap in closes.items():
        seq = [cmap.get(t) for t in times]
        rs = {}
        for i in range(h, len(seq)):
            a, b = seq[i - h], seq[i]
            if a and b and a > 0 and b > 0:
                rs[i] = math.log(b / a)
        ret[sid] = rs
    return ret


def corr_and_loadings(ret):
    sids = sorted(ret)
    corrs = []
    for a in range(len(sids)):
        for b in range(a + 1, len(sids)):
            ra, rb = ret[sids[a]], ret[sids[b]]
            common = sorted(set(ra) & set(rb))
            if len(common) >= 8:
                c = pearson([ra[i] for i in common], [rb[i] for i in common])
                if c is not None:
                    corrs.append(c)
    # common factor = equal-weight mean return per timestamp; per-stock R^2 vs factor
    idxs = set()
    for rs in ret.values():
        idxs.update(rs)
    factor = {}
    for i in idxs:
        vals = [ret[s][i] for s in sids if i in ret[s]]
        if len(vals) >= 3:
            factor[i] = sum(vals) / len(vals)
    loadings = []
    for s in sids:
        common = sorted(set(ret[s]) & set(factor))
        if len(common) >= 8:
            c = pearson([ret[s][i] for i in common], [factor[i] for i in common])
            if c is not None:
                loadings.append(c * c)
    return corrs, loadings


def pairwise_by_sector(ret, nsec):
    """Split pairwise return correlations into INTRA-sector (same stockId%nsec) vs CROSS-sector.
    Sector pulses should lift INTRA clearly above CROSS = the real-market signature. This within-arm
    contrast is far less window-noise-sensitive than absolute corr (both groups share the same window)."""
    sids = sorted(ret)
    intra, cross = [], []
    for a in range(len(sids)):
        for b in range(a + 1, len(sids)):
            sa, sb = sids[a], sids[b]
            try:
                same = (int(sa) % nsec) == (int(sb) % nsec)  # MUST match the engine: sector = stockId % SectorCount
            except ValueError:
                continue  # non-numeric stock id ⇒ sector undefined ⇒ skip (metric is engine-id-based)
            ra, rb = ret[sa], ret[sb]
            common = sorted(set(ra) & set(rb))
            if len(common) >= 8:
                c = pearson([ra[i] for i in common], [rb[i] for i in common])
                if c is not None:
                    (intra if same else cross).append(c)
    return intra, cross


def load_ohlcv(path):
    """Full OHLCV rows per stock, for range-efficiency (bodies vs wicks) + volume concentration."""
    with open(path, newline="", encoding="utf-8") as fh:
        lines = [ln for ln in fh if not ln.lstrip().startswith("#")]
    rdr = csv.DictReader(lines)
    cols = {c.lower(): c for c in (rdr.fieldnames or [])}
    def pick(*names):
        for n in names:
            if n in cols:
                return cols[n]
        return None
    cs = pick("stockid", "stock_id", "sid")
    co, ch, cl, cc = pick("open"), pick("high"), pick("low"), pick("close")
    cv = pick("volume", "vol")
    rows = []
    if not (cs and co and ch and cl and cc):
        return rows
    for r in rdr:
        try:
            rows.append((str(r[cs]).strip(), float(r[co]), float(r[ch]), float(r[cl]),
                         float(r[cc]), float(r[cv]) if cv else 0.0))
        except (ValueError, KeyError, TypeError):
            continue
    return rows


def body_volume_report(path):
    rows = load_ohlcv(path)
    if not rows:
        return
    effs, vol_by_stock = [], {}
    for sid, o, h, l, c, v in rows:
        if h > l:
            effs.append(abs(c - o) / (h - l))
        vol_by_stock[sid] = vol_by_stock.get(sid, 0.0) + v
    if effs:
        print("RANGE-EFFICIENCY |close-open|/(high-low) (bodies vs wicks; higher = stickier, less wick):")
        print("  mean=%.3f  median=%.3f   (real equity ~0.3-0.5; ~0.1-0.2 = wick-dominated)"
              % (statistics.mean(effs), statistics.median(effs)))
    vols = sorted((v for v in vol_by_stock.values()), reverse=True)
    tot = sum(vols)
    if tot > 0 and len(vols) >= 5:
        n = len(vols)
        top5 = sum(vols[:5]) / tot
        mean_v = tot / n
        stale = sum(1 for v in vols if v < 0.05 * mean_v)
        asc = sorted(vols)
        gini = sum((2 * (i + 1) - n - 1) * x for i, x in enumerate(asc)) / (n * tot)
        print("VOLUME CONCENTRATION (variable volume on movers; cure staleness):")
        print("  stocks=%d  top5-share=%.2f  gini=%.3f  stale(<5%%-of-mean)=%d   (higher gini/top5 = more concentrated)"
              % (n, top5, gini, stale))


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--csv", required=True)
    ap.add_argument("--currency", default="USD")
    ap.add_argument("--horizons", default="1,5")
    ap.add_argument("--sectors", type=int, default=0,
                    help="if >0, also report INTRA- vs CROSS-sector corr with sector = stockId %% N (set N = the engine SectorCount)")
    args = ap.parse_args()
    horizons = [int(h) for h in args.horizons.split(",") if h.strip()]

    rows, used = load(args.csv, args.currency)
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

    print("=== cross-stock diagnostic ===")
    print("csv         :", args.csv.split("/")[-1].split("\\")[-1])
    print("columns used:", used)
    print("stocks      :", len(closes), " timeline buckets:", len(times))
    print()
    print("PAIRWISE RETURN CORRELATION (cross-stock co-movement) — real equity ~+0.20..+0.50; ~0 = independent:")
    for h in horizons:
        corrs, loadings = corr_and_loadings(returns_at(closes, times, h))
        if corrs:
            corrs.sort()
            print("  h=%2dmin  pairs=%4d  mean=%+.3f  median=%+.3f  p10=%+.3f  p90=%+.3f   factorR2 mean=%.3f"
                  % (h, len(corrs), statistics.mean(corrs), statistics.median(corrs),
                     corrs[len(corrs)//10], corrs[len(corrs)*9//10],
                     statistics.mean(loadings) if loadings else float("nan")))

    if args.sectors and args.sectors > 1:
        print()
        print("INTRA vs CROSS-SECTOR CORRELATION (sector = stockId %% %d; a sector pulse should lift INTRA clearly above CROSS):"
              % args.sectors)
        for h in horizons:
            intra, cross = pairwise_by_sector(returns_at(closes, times, h), args.sectors)
            mi = statistics.mean(intra) if intra else float("nan")
            mc = statistics.mean(cross) if cross else float("nan")
            print("  h=%2dmin  intra(n=%4d) mean=%+.3f   cross(n=%4d) mean=%+.3f   intra-minus-cross=%+.3f"
                  % (h, len(intra), mi, len(cross), mc, mi - mc))
    # kurtosis on 1-bucket returns
    r1 = returns_at(closes, times, 1)
    kurts = [k for s in sorted(r1) if (k := excess_kurt(list(r1[s].values()))) is not None]
    if kurts:
        kurts.sort()
        print("EXCESS KURTOSIS 1-min returns (real ~+3..+8; 0=Gaussian): mean=%+.2f median=%+.2f p90=%+.2f"
              % (statistics.mean(kurts), statistics.median(kurts), kurts[len(kurts)*9//10]))

    body_volume_report(args.csv)


if __name__ == "__main__":
    main()
