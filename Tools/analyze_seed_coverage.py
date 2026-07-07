# SCRATCH (uncommitted): analyze the generated seed's per-book watchlist coverage by currency.
# The seed-level root of book depth = how many home-currency bots watchlist each book.
import openpyxl
from collections import defaultdict
from Config import STOCKS, CROSS_LISTED_STOCK_IDS, EUR_ONLY_STOCK_IDS

wb = openpyxl.load_workbook(
    "../KieshStockExchange.Server/Resources/Raw/AIUserData.xlsx", read_only=True, data_only=True)
prof = wb["Profile"]
it = prof.iter_rows(values_only=True)
header = [str(h).strip() if h is not None else "" for h in next(it)]

def find_col(options):
    low = [h.lower() for h in header]
    for opt in options:
        if opt.lower() in low:
            return low.index(opt.lower())
    raise KeyError(f"{options} not in {header}")

wl_col = find_col(["Watchlist", "watchlist_csv", "WatchlistCsv"])
hc_col = find_col(["HomeCurrency", "home_currency", "HomeCurrencyType"])

cov = defaultdict(lambda: {"USD": 0, "EUR": 0})
nbots = {"USD": 0, "EUR": 0}
for r in it:
    hc = str(r[hc_col]).strip()
    if hc not in ("USD", "EUR"):
        continue
    nbots[hc] += 1
    for sid in str(r[wl_col] or "").split(","):
        sid = sid.strip()
        if sid.isdigit():
            cov[int(sid)][hc] += 1

cross, eur = set(CROSS_LISTED_STOCK_IDS), set(EUR_ONLY_STOCK_IDS)
def listing(sid):
    return "DUAL" if sid in cross else "EUR-only" if sid in eur else "USD-only"

print(f"Bots: USD={nbots['USD']}  EUR={nbots['EUR']}  (ratio {nbots['USD']/max(1,nbots['EUR']):.2f})")
print(f"{'sid':>3} {'tkr':<6} {'listing':<8} {'USDwatch':>8} {'EURwatch':>8}")
for sid in sorted(STOCKS):
    print(f"{sid:>3} {STOCKS[sid]['ticker']:<6} {listing(sid):<8} {cov[sid]['USD']:>8} {cov[sid]['EUR']:>8}")

eur_books = [s for s in STOCKS if listing(s) in ("DUAL", "EUR-only")]
usd_books = [s for s in STOCKS if listing(s) in ("DUAL", "USD-only")]
eonly = [s for s in STOCKS if listing(s) == "EUR-only"]
ec = {s: cov[s]['EUR'] for s in eur_books}
uc = {s: cov[s]['USD'] for s in usd_books}
eoc = {s: cov[s]['EUR'] for s in eonly}
print(f"\nEUR books ({len(eur_books)}): min EUR-watchers={min(ec.values())} (sid {min(ec, key=ec.get)}), max={max(ec.values())}")
print(f"  of which EUR-ONLY smalls ({len(eonly)}): min={min(eoc.values())} (sid {min(eoc, key=eoc.get)}), median~{sorted(eoc.values())[len(eoc)//2]}, max={max(eoc.values())}")
print(f"USD books ({len(usd_books)}): min USD-watchers={min(uc.values())}, max={max(uc.values())}")
stranded_eur = sum(cov[s]['EUR'] for s in STOCKS if listing(s) == "USD-only")
stranded_usd = sum(cov[s]['USD'] for s in STOCKS if listing(s) == "EUR-only")
print(f"Cross-currency watch (MUST be 0 after the gate): EUR-bots-on-USD-only={stranded_eur}, USD-bots-on-EUR-only={stranded_usd}")
