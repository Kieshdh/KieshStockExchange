#!/usr/bin/env python3
"""Intra-sector vs inter-sector cross-stock correlation on soak candle CSVs.

The sector test: with REAL sectors, stocks in the SAME sector should co-move MORE than stocks
in DIFFERENT sectors (intra-corr > inter-corr). A market with no sector structure shows intra
≈ inter. Reads the sector assignment straight from Tools/Config.py (STOCKS[id]["sector"]).

Returns are computed on a shared minute grid (aligns stocks by absolute bucket time, so gaps
don't smear the horizon). One currency only (default USD) so stock-vs-stock isn't conflated
with the ~1.0 USD/EUR same-stock correlation. Pure-Python, ASCII output.

Usage: py scripts/sector_corr.py --csv data/soaks/candles-XXX.csv [--currency USD] [--horizons 1,5,10]
"""
import argparse, csv, math, os, statistics, sys
from collections import defaultdict


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
    return rows


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


def sector_map():
    sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "Tools"))
    import Config
    smap = {sid: s.get("sector", "?") for sid, s in Config.STOCKS.items()}
    return smap, list(getattr(Config, "SECTORS", sorted(set(smap.values()))))


def returns_on_grid(rows, horizon):
    """Per-stock {grid_index: log-return over `horizon` buckets}, aligned on absolute bucket time."""
    times = sorted({t for _, t, _ in rows})
    ti = {t: i for i, t in enumerate(times)}
    close = defaultdict(dict)
    for sid, t, c in rows:
        close[sid][ti[t]] = c
    ret = {}
    for sid, cser in close.items():
        rr = {}
        for i, c1 in cser.items():
            c0 = cser.get(i - horizon)
            if c0 and c0 > 0 and c1 > 0:
                rr[i] = math.log(c1 / c0)
        if rr:
            ret[sid] = rr
    return ret


def mean(xs):
    return sum(xs) / len(xs) if xs else float("nan")


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--csv", required=True)
    ap.add_argument("--currency", default="USD")
    ap.add_argument("--horizons", default="1,5,10")
    ap.add_argument("--min-overlap", type=int, default=5)
    args = ap.parse_args()

    rows = load(args.csv, args.currency)
    smap, sectors = sector_map()

    def sec_of(sid):
        try:
            return smap.get(int(sid))
        except (ValueError, TypeError):
            return None

    print("\n=== intra- vs inter-SECTOR correlation — %s ===" % os.path.basename(args.csv))
    print("sectors (%d): %s" % (len(sectors), ", ".join(sectors)))
    for H in [int(h) for h in args.horizons.split(",")]:
        ret = returns_on_grid(rows, H)
        stocks = sorted(ret, key=lambda s: (int(s) if s.isdigit() else 1 << 30, s))
        intra, inter = [], []
        per_sector = defaultdict(list)
        for i in range(len(stocks)):
            for j in range(i + 1, len(stocks)):
                a, b = stocks[i], stocks[j]
                common = sorted(set(ret[a]) & set(ret[b]))
                if len(common) < args.min_overlap:
                    continue
                r = pearson([ret[a][t] for t in common], [ret[b][t] for t in common])
                if r is None:
                    continue
                sa, sb = sec_of(a), sec_of(b)
                if sa is not None and sa == sb:
                    intra.append(r); per_sector[sa].append(r)
                else:
                    inter.append(r)
        gap = mean(intra) - mean(inter)
        print("\n h=%2dmin  intra-pairs=%d inter-pairs=%d" % (H, len(intra), len(inter)))
        print("   INTRA-sector mean corr = %+.3f   INTER-sector mean corr = %+.3f   GAP = %+.3f  %s"
              % (mean(intra), mean(inter), gap,
                 "(sectors co-move)" if gap > 0.02 else "(no sector structure)"))
        if H == max(int(h) for h in args.horizons.split(",")):
            print("   per-sector intra-corr (this horizon):")
            for s in sectors:
                vals = per_sector.get(s, [])
                if vals:
                    print("     %-26s %+.3f  (n=%d pairs)" % (s, mean(vals), len(vals)))


if __name__ == "__main__":
    main()
