import re
import sys
from datetime import datetime, timedelta
from pathlib import Path

import matplotlib
matplotlib.use("Agg")
import matplotlib.pyplot as plt
import matplotlib.dates as mdates

LOG = Path(sys.argv[1]) if len(sys.argv) > 1 else Path("logs/fix-soak-20260530-042919.log")
OUT = Path(sys.argv[2]) if len(sys.argv) > 2 else Path("docs/active-bots-2026-05-30.png")

# [05:04:58 INF] BotStats[60s] @ 05:04:58: bots 20000/20000, trades 35800 ...
line_re = re.compile(r"BotStats\[60s\] @ (\d{2}:\d{2}:\d{2}): bots (\d+)/(\d+)")

times, active, cap = [], [], None
base = None  # roll HH:mm:ss into a continuous timeline across any midnight wrap
prev = None
for raw in LOG.read_text(encoding="utf-8", errors="replace").splitlines():
    m = line_re.search(raw)
    if not m:
        continue
    t = datetime.strptime(m.group(1), "%H:%M:%S")
    if base is None:
        base = t.date()
    dt = datetime.combine(base, t.time())
    if prev is not None and dt < prev:      # passed midnight → next day
        base = base + timedelta(days=1)
        dt = datetime.combine(base, t.time())
    prev = dt
    times.append(dt)
    active.append(int(m.group(2)))
    cap = int(m.group(3))

if not times:
    print("No BotStats lines found.")
    sys.exit(1)

fig, ax = plt.subplots(figsize=(13, 5.5))
ax.plot(times, active, color="#2E86DE", linewidth=1.4, label="Active bots")
ax.axhline(cap, color="#aa3333", linestyle="--", linewidth=1, label=f"Cap ({cap:,})")

ax.set_title("Active bots over the fix-verification soak — 2026-05-30", fontsize=13)
ax.set_xlabel("Time")
ax.set_ylabel("Active bots")
ax.xaxis.set_major_formatter(mdates.DateFormatter("%H:%M"))
ax.xaxis.set_major_locator(mdates.MinuteLocator(byminute=range(0, 60, 30)))
fig.autofmt_xdate()
ax.set_ylim(0, cap * 1.05)
ax.grid(True, alpha=0.3)
ax.legend(loc="upper right")

dur = times[-1] - times[0]
peak = max(active)
mean = sum(active) / len(active)
ax.text(0.01, 0.02,
        f"{len(active)} samples · {dur} span · peak {peak:,} · mean {mean:,.0f}",
        transform=ax.transAxes, fontsize=9, color="#555")

OUT.parent.mkdir(parents=True, exist_ok=True)
fig.tight_layout()
fig.savefig(OUT, dpi=130)
print(f"Wrote {OUT}  ({len(active)} points, {times[0].strftime('%H:%M')}–{times[-1].strftime('%H:%M')}, peak {peak:,}, mean {mean:,.0f})")
