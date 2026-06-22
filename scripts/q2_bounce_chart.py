# Q2 visual: bounce-mid ON vs OFF on the SAME trades. Reads kse_bnc_mid2 (has both Price + MidPrice),
# builds 1-min candle CLOSE two ways — last-trade Price (OFF / today) vs mid-price (ON / baked) — and overlays
# them per stock so the bounce-removal (smoother close, less tick zig-zag) is visible on an identical price path.
import subprocess, sys
from collections import defaultdict
import matplotlib
matplotlib.use("Agg")
import matplotlib.pyplot as plt

PG = "kieshstockexchange-postgres-1"
DB = sys.argv[1] if len(sys.argv) > 1 else "kse_bnc_mid2"
BUCKET = 60
SLICE = 60   # show a steady-state slice of N candles so the per-minute bounce zig-zag is visible
STOCKS = [(1, "MSFT"), (3, "AAPL"), (4, "AMZN")]

def candles(stock):
    sql = (
        f'SELECT floor(extract(epoch from t."Timestamp")/{BUCKET}) AS b, '
        '(array_agg(t."Price" ORDER BY t."Timestamp" DESC))[1] AS close_last, '
        '(array_agg(COALESCE(t."MidPrice",t."Price") ORDER BY t."Timestamp" DESC))[1] AS close_mid '
        'FROM "Transactions" t '
        f'WHERE t."StockId"={stock} '
        'GROUP BY b ORDER BY b;')
    out = subprocess.run(["docker","exec","-i",PG,"psql","-U","kse","-d",DB,"--csv","-c",sql],
                         capture_output=True, text=True)
    if out.returncode != 0:
        sys.exit(f"psql failed: {out.stderr.strip()}")
    last, mid = [], []
    for ln in out.stdout.splitlines()[1:]:
        p = ln.split(",")
        if len(p) < 3 or not p[1] or not p[2]:
            continue
        last.append(float(p[1])); mid.append(float(p[2]))
    return last, mid

fig, axes = plt.subplots(len(STOCKS), 1, figsize=(13, 4*len(STOCKS)))
for ax, (sid, label) in zip(axes, STOCKS):
    last, mid = candles(sid)
    s = max(0, (len(last) - SLICE) // 2)   # centered steady-state slice
    last, mid = last[s:s+SLICE], mid[s:s+SLICE]
    x = list(range(len(last)))
    ax.plot(x, last, color="#d62728", lw=1.1, alpha=0.85, label="CLOSE = last-trade (OFF / today)")
    ax.plot(x, mid,  color="#1f77b4", lw=1.1, alpha=0.95, label="CLOSE = mid-price (ON / baked)")
    ax.set_title(f"{label}  —  stock {sid}  ({len(last)} 1-min candles)")
    ax.set_xlabel("minute"); ax.set_ylabel("close price")
    ax.legend(loc="best", fontsize=8); ax.grid(alpha=0.25)
fig.suptitle("Q2: bounce-mid candle CLOSE — last-trade (OFF) vs mid-price (ON), identical trades (kse_bnc_mid2)",
             fontsize=12, y=0.995)
fig.tight_layout()
out_png = sys.argv[2] if len(sys.argv) > 2 else "logs/q2_bounce_on_vs_off.png"
fig.savefig(out_png, dpi=110)
print(f"wrote {out_png}")
