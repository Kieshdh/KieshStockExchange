#!/usr/bin/env python3
"""Step-0 feasibility: do fat 1-min RETURN tails fit under the geometric LEVEL cap?

Measures per-stock 1-min log-return sigma + excess kurtosis from a candle CSV, then
computes how many sigma a single 1-min move needs to breach the +-ln(3) (x3 / ÷3) LEVEL
cap. The council claim under test: fat tails live in the RETURN distribution, the cap
bounds the cumulative LEVEL, so kurtosis-10 return tails fit far under the cap.

usage: py scripts/return_headroom.py <candles.csv> [more.csv ...]
"""
import sys, csv, math, statistics


def load(path):
    series = {}
    with open(path, newline="") as fh:
        for row in csv.reader(fh):
            if not row or row[0].startswith("#") or row[0] == "stock_id":
                continue
            sid, bucket, close = int(row[0]), int(row[1]), float(row[5])
            series.setdefault(sid, []).append((bucket, close))
    return series


def excess_kurtosis(xs):
    n = len(xs)
    m = sum(xs) / n
    var = sum((x - m) ** 2 for x in xs) / n
    if var <= 0:
        return float("nan")
    return sum((x - m) ** 4 for x in xs) / n / var ** 2 - 3.0


def autocorr_lag1(xs):
    n = len(xs)
    m = sum(xs) / n
    denom = sum((x - m) ** 2 for x in xs)
    if denom <= 0:
        return float("nan")
    num = sum((xs[i] - m) * (xs[i - 1] - m) for i in range(1, n))
    return num / denom


def analyze(path):
    series = load(path)
    sigmas, kurts, acfs = [], [], []
    for sid, pts in series.items():
        pts.sort()
        closes = [c for _, c in pts]
        rets = [math.log(closes[i] / closes[i - 1])
                for i in range(1, len(closes)) if closes[i - 1] > 0 and closes[i] > 0]
        if len(rets) < 15:
            continue
        s = statistics.pstdev(rets)
        if s > 0:
            sigmas.append(s)
            kurts.append(excess_kurtosis(rets))
            acfs.append(autocorr_lag1(rets))
    med_sigma = statistics.median(sigmas)
    med_kurt = statistics.median(kurts)
    med_acf = statistics.median(acfs)
    cap = math.log(3.0)  # +1.0986 log-return reaches x3; -1.0986 reaches ÷3
    print(f"\n=== {path.split(chr(92))[-1]} ===")
    print(f"stocks={len(sigmas)}")
    print(f"median per-stock 1-min return sigma = {med_sigma*100:.3f}%  (log {med_sigma:.5f})")
    print(f"median per-stock 1-min ret_acf lag-1 = {med_acf:+.3f}   (real 1-min target: -0.05 to -0.10)")
    print(f"median per-stock 1-min excess kurtosis = {med_kurt:+.2f}   (real 1-min target: 10+)")
    print(f"1-min move at median sigma:  3s={3*med_sigma*100:.1f}%  5s={5*med_sigma*100:.1f}%  10s={10*med_sigma*100:.1f}%")
    print(f"x3/÷3 LEVEL cap = +-{cap:.3f} log  (=+200% / -67%)")
    print(f"single-step 1-min move needed to breach the cap = {cap/med_sigma:.0f} sigma")
    print(f"=> a kurtosis-10 tail sits at ~5-10 sigma ({5*med_sigma*100:.1f}-{10*med_sigma*100:.1f}%), "
          f"FAR under the {cap/med_sigma:.0f}-sigma level cap.")


for p in sys.argv[1:]:
    analyze(p)
