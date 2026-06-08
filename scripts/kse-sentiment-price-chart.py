# Export aligned bot-sentiment + price data for one soak/harness run and render a
# liveliness-vs-sentiment correlation chart. Sentiment comes from the server's
# data/telemetry/bot_sentiment.ndjson (60s cadence); price-over-time comes from the
# kse_soak Postgres Transactions (bucketed to 60s, primary-currency listing). Both are
# UTC so they join directly. Outputs: a merged CSV + a PNG grid (one stock per cell,
# dual-axis sentiment vs %-deviation-from-seed), with stocks chosen across the
# Calm/Normal/Volatile/Meme personality classes the server assigns by id-hash.
#
# Usage:
#   python scripts/kse-sentiment-price-chart.py [--window-min 25] [--db kse_soak]
#          [--stocks 1,12,33,...] [--out-dir logs]
import argparse, json, subprocess, sys, time
from collections import defaultdict
from datetime import datetime, timezone
from pathlib import Path
import matplotlib
matplotlib.use("Agg")
import matplotlib.pyplot as plt

ROOT = Path(__file__).resolve().parent.parent
NDJSON = ROOT / "KieshStockExchange.Server" / "data" / "telemetry" / "bot_sentiment.ndjson"
PG = "kieshstockexchange-postgres-1"

# --- personality classification: mirror StockProfileService.Get exactly ---
def avalanche(sid: int) -> int:
    M = (1 << 64) - 1
    h = (sid * 0x9E3779B97F4A7C15 + 0x165667B19E3779F9) & M
    h ^= h >> 33; h = (h * 0xFF51AFD7ED558CCD) & M; h ^= h >> 33
    return h

def stock_class(sid: int) -> str:
    if 1 <= sid <= 5:
        return "Calm"
    b = avalanche(sid) % 100
    return "Calm" if b < 35 else "Normal" if b < 75 else "Volatile" if b < 93 else "Meme"

def pearson(xs, ys):
    n = len(xs)
    if n < 3:
        return None
    mx = sum(xs) / n; my = sum(ys) / n
    sxx = sum((x - mx) ** 2 for x in xs); syy = sum((y - my) ** 2 for y in ys)
    sxy = sum((x - mx) * (y - my) for x, y in zip(xs, ys))
    if sxx <= 0 or syy <= 0:
        return None
    return sxy / (sxx ** 0.5 * syy ** 0.5)

def aligned(sid, sent, price, seed, bucket):
    # join sentiment + price-dev% on the shared time-bucket grid -> sorted (t, sentiment, dev%)
    sp = seed.get(sid, 0.0)
    if sp <= 0:
        return []
    sd = {int(e // bucket) * bucket: v for e, v in sent.get(sid, [])}
    rows = []
    for b, px in price.get(sid, []):
        key = int(b)
        if key in sd:
            rows.append((key, sd[key], (px - sp) / sp * 100))
    rows.sort()
    return rows

def correlations(rows):
    # r_level = corr(sentiment, dev%); r_lead = corr(sentiment(t), dev%(t+1)-dev%(t))
    if len(rows) < 4:
        return None, None
    s = [r[1] for r in rows]; d = [r[2] for r in rows]
    r_level = pearson(s, d)
    ret = [d[i + 1] - d[i] for i in range(len(d) - 1)]
    r_lead = pearson(s[:-1], ret)
    return r_level, r_lead

def iso_to_epoch(s: str) -> float:
    # TimestampUtc like 2026-06-08T10:38:13.3066868Z (trim to microseconds for fromisoformat)
    s = s.rstrip("Z")
    if "." in s:
        head, frac = s.split("."); s = head + "." + frac[:6]
    return datetime.fromisoformat(s).replace(tzinfo=timezone.utc).timestamp()

def load_sentiment(since_epoch: float):
    # stockId -> list[(epoch, combined)]; plus global series [(epoch, globalSum)]
    per = defaultdict(list); glob = {}
    with open(NDJSON, "r", encoding="utf-8") as f:
        for line in f:
            line = line.strip()
            if not line:
                continue
            try:
                r = json.loads(line)
            except json.JSONDecodeError:
                continue
            e = iso_to_epoch(r["TimestampUtc"])
            if e < since_epoch:
                continue
            per[int(r["StockId"])].append((e, float(r["Combined"])))
            glob[round(e)] = float(r["GlobalSum"])
    for sid in per:
        per[sid].sort()
    return per, sorted(glob.items())

def load_prices(db: str, since_epoch: float, bucket: int):
    # stockId -> (seed, list[(bucket_epoch, last_price)]) for the primary-currency listing
    sql = (
        'SELECT t."StockId", '
        f'floor(extract(epoch from t."Timestamp")/{bucket})*{bucket} AS b, '
        '(array_agg(t."Price" ORDER BY t."Timestamp" DESC))[1] AS px, '
        'l."SeedPrice" '
        'FROM "Transactions" t '
        'JOIN "StockListings" l ON l."StockId"=t."StockId" AND l."Currency"=t."Currency" AND l."IsPrimary"=true '
        f"WHERE extract(epoch from t.\"Timestamp\") >= {since_epoch:.0f} "
        'GROUP BY t."StockId", b, l."SeedPrice" ORDER BY t."StockId", b;'
    )
    out = subprocess.run(["docker", "exec", "-i", PG, "psql", "-U", "kse", "-d", db, "--csv", "-c", sql],
                         capture_output=True, text=True)
    if out.returncode != 0:
        sys.exit(f"psql failed: {out.stderr.strip()}")
    series = defaultdict(list); seed = {}
    for ln in out.stdout.splitlines()[1:]:  # skip header
        parts = ln.split(",")
        if len(parts) < 4:
            continue
        sid, b, px, sp = int(parts[0]), float(parts[1]), float(parts[2]), float(parts[3])
        series[sid].append((b, px)); seed[sid] = sp
    return series, seed

def pick_representatives(price_series, n_per_class=1):
    # stocks that actually traded, grouped by class, busiest first (most buckets)
    by_class = defaultdict(list)
    for sid, pts in price_series.items():
        by_class[stock_class(sid)].append((len(pts), sid))
    chosen = []
    for cls in ("Calm", "Normal", "Volatile", "Meme"):
        for _, sid in sorted(by_class.get(cls, []), reverse=True)[:n_per_class]:
            chosen.append(sid)
    return chosen

def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--window-min", type=float, default=70.0, help="minutes back from the latest sentiment sample")
    ap.add_argument("--bucket-sec", type=int, default=15, help="price/sentiment alignment bucket (match the sentiment cadence)")
    ap.add_argument("--db", default="kse_soak")
    ap.add_argument("--stocks", default="", help="explicit comma-separated stock ids (else auto by class)")
    ap.add_argument("--per-class", type=int, default=1)
    ap.add_argument("--out-dir", default="logs")
    args = ap.parse_args()

    if not NDJSON.exists():
        sys.exit(f"sentiment file not found: {NDJSON}")

    # anchor the window on the newest sentiment sample so it tracks the latest run
    last_ts = 0.0
    with open(NDJSON, "r", encoding="utf-8") as f:
        for line in f:
            line = line.strip()
            if line:
                try:
                    last_ts = max(last_ts, iso_to_epoch(json.loads(line)["TimestampUtc"]))
                except Exception:
                    pass
    since = last_ts - args.window_min * 60
    print(f"window: {datetime.fromtimestamp(since, timezone.utc):%H:%M} -> "
          f"{datetime.fromtimestamp(last_ts, timezone.utc):%H:%M} UTC ({args.window_min:.0f} min)")

    sent, glob = load_sentiment(since)
    price, seed = load_prices(args.db, since, args.bucket_sec)

    stocks = ([int(x) for x in args.stocks.split(",") if x.strip()]
              if args.stocks else pick_representatives(price, args.per_class))
    if not stocks:
        sys.exit("no traded stocks in the window")
    print("stocks:", ", ".join(f"{s}({stock_class(s)})" for s in stocks))

    # market-wide correlation summary by personality class (all traded stocks with enough points)
    by_class = defaultdict(lambda: {"level": [], "lead": [], "n": 0})
    for sid in price:
        rl, rd = correlations(aligned(sid, sent, price, seed, args.bucket_sec))
        g = by_class[stock_class(sid)]
        g["n"] += 1
        if rl is not None: g["level"].append(rl)
        if rd is not None: g["lead"].append(rd)
    print("\nclass        stocks  r(level)  r(lead)   (mean Pearson across class)")
    for cls in ("Calm", "Normal", "Volatile", "Meme"):
        g = by_class.get(cls)
        if not g or g["n"] == 0:
            continue
        ml = sum(g["level"]) / len(g["level"]) if g["level"] else float("nan")
        md = sum(g["lead"]) / len(g["lead"]) if g["lead"] else float("nan")
        print(f"{cls:<11}  {g['n']:>5}   {ml:>7.3f}  {md:>7.3f}")

    out_dir = ROOT / args.out_dir; out_dir.mkdir(exist_ok=True)
    stamp = datetime.now().strftime("%Y%m%d-%H%M%S")

    # merged long CSV
    csv_path = out_dir / f"sentiment-price-{stamp}.csv"
    with open(csv_path, "w", encoding="utf-8") as f:
        f.write("stockId,class,t_utc,sentiment,price,seed,dev_pct\n")
        bsec = args.bucket_sec
        for sid in stocks:
            # bucket sentiment onto the same grid as price so the CSV rows join cleanly
            sdict = {int(e // bsec) * bsec: v for e, v in sent.get(sid, [])}
            sp = seed.get(sid, 0.0)
            for b, px in price.get(sid, []):
                sv = sdict.get(int(b), "")
                dev = (px - sp) / sp * 100 if sp else ""
                f.write(f"{sid},{stock_class(sid)},{datetime.fromtimestamp(b, timezone.utc):%H:%M:%S},"
                        f"{sv},{px:.4f},{sp:.4f},{dev:.3f}\n")

    # chart grid: dual-axis sentiment (left) vs %-dev-from-seed (right)
    n = len(stocks); cols = 2; rows = (n + cols - 1) // cols
    fig, axes = plt.subplots(rows, cols, figsize=(13, 3.6 * rows), squeeze=False)
    t0 = since
    for i, sid in enumerate(stocks):
        ax = axes[i // cols][i % cols]
        sp = seed.get(sid, 0.0)
        st = sent.get(sid, [])
        if st:
            xs = [(e - t0) / 60 for e, _ in st]; ys = [v for _, v in st]
            ax.plot(xs, ys, color="tab:blue", lw=1.3, label="sentiment")
        ax.axhline(0, color="gray", lw=0.6, ls=":")
        ax.set_ylabel("sentiment", color="tab:blue"); ax.tick_params(axis="y", labelcolor="tab:blue")
        ax.set_ylim(-2.2, 2.2)
        ax2 = ax.twinx()
        pr = price.get(sid, [])
        if pr and sp:
            xs = [(b - t0) / 60 for b, _ in pr]; ys = [(px - sp) / sp * 100 for _, px in pr]
            ax2.plot(xs, ys, color="tab:red", lw=1.4, label="price dev %")
        ax2.set_ylabel("price dev % vs seed", color="tab:red"); ax2.tick_params(axis="y", labelcolor="tab:red")
        rl, rd = correlations(aligned(sid, sent, price, seed, args.bucket_sec))
        rtxt = f"  r(level)={rl:+.2f}" if rl is not None else ""
        rtxt += f"  r(lead)={rd:+.2f}" if rd is not None else ""
        ax.set_title(f"stock {sid} [{stock_class(sid)}]{rtxt}"); ax.set_xlabel("minutes into window")
    for j in range(n, rows * cols):
        axes[j // cols][j % cols].axis("off")
    fig.suptitle("Bot sentiment vs price deviation (liveliness correlation)", fontsize=13)
    fig.tight_layout(rect=[0, 0, 1, 0.97])
    png_path = out_dir / f"sentiment-price-{stamp}.png"
    fig.savefig(png_path, dpi=110)
    print(f"wrote:\n  {csv_path}\n  {png_path}")

if __name__ == "__main__":
    main()
