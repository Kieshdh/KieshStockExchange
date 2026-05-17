# ─────────────────────────────── Stock universe ──────────────────────────────

# Ordered roughly by market cap descending (largest first)
STOCKS = {
    # Mega-cap tech (top 10 by market cap)
     1: {"ticker": "MSFT",  "name": "Microsoft Corporation",                "price":  513.71},
     2: {"ticker": "NVDA",  "name": "NVIDIA Corporation",                   "price":  173.50},
     3: {"ticker": "AAPL",  "name": "Apple Inc.",                           "price":  213.88},
     4: {"ticker": "AMZN",  "name": "Amazon.com, Inc.",                     "price":  231.44},
     5: {"ticker": "GOOG",  "name": "Alphabet Inc.",                        "price":  194.08},
     6: {"ticker": "META",  "name": "Meta Platforms, Inc.",                 "price":  712.68},
     7: {"ticker": "AVGO",  "name": "Broadcom Inc.",                        "price":  290.18},
     8: {"ticker": "TSLA",  "name": "Tesla, Inc.",                          "price":  316.06},
     9: {"ticker": "TSM",   "name": "Taiwan Semiconductor Manufacturing",   "price":  245.60},
    10: {"ticker": "BRK.B", "name": "Berkshire Hathaway Inc.",              "price":  478.50},
    # Mega-cap mixed (11-20)
    11: {"ticker": "LLY",   "name": "Eli Lilly & Co",                       "price":  812.69},
    12: {"ticker": "WMT",   "name": "Walmart Inc.",                         "price":   97.47},
    13: {"ticker": "JPM",   "name": "JPMorgan Chase & Co.",                 "price":  285.00},
    14: {"ticker": "V",     "name": "Visa Inc.",                            "price":  357.04},
    15: {"ticker": "ORCL",  "name": "Oracle Corporation",                   "price":  245.12},
    16: {"ticker": "MA",    "name": "Mastercard Incorporated",              "price":  568.22},
    17: {"ticker": "XOM",   "name": "Exxon Mobil Corporation",              "price":  115.00},
    18: {"ticker": "UNH",   "name": "UnitedHealth Group Incorporated",      "price":  580.00},
    19: {"ticker": "JNJ",   "name": "Johnson & Johnson",                    "price":  165.00},
    20: {"ticker": "COST",  "name": "Costco Wholesale Corporation",         "price":  950.00},
    # Large-cap (21-30)
    21: {"ticker": "NFLX",  "name": "Netflix, Inc.",                        "price": 1180.49},
    22: {"ticker": "PG",    "name": "The Procter & Gamble Company",         "price":  168.00},
    23: {"ticker": "HD",    "name": "The Home Depot, Inc.",                 "price":  410.00},
    24: {"ticker": "BAC",   "name": "Bank of America Corporation",          "price":   48.45},
    25: {"ticker": "ABBV",  "name": "AbbVie Inc.",                          "price":  215.00},
    26: {"ticker": "CRM",   "name": "Salesforce, Inc.",                     "price":  305.00},
    27: {"ticker": "ASML",  "name": "ASML Holding N.V.",                    "price":  711.25},
    28: {"ticker": "CVX",   "name": "Chevron Corporation",                  "price":  165.00},
    29: {"ticker": "KO",    "name": "The Coca-Cola Company",                "price":   69.17},
    30: {"ticker": "WFC",   "name": "Wells Fargo & Company",                "price":   78.00},
    # Large-cap (31-40)
    31: {"ticker": "PEP",   "name": "PepsiCo, Inc.",                        "price":  152.00},
    32: {"ticker": "ADBE",  "name": "Adobe Inc.",                           "price":  425.00},
    33: {"ticker": "BABA",  "name": "Alibaba Group Holding Limited",        "price":  120.03},
    34: {"ticker": "MCD",   "name": "McDonald's Corporation",               "price":  298.47},
    35: {"ticker": "TMO",   "name": "Thermo Fisher Scientific Inc.",        "price":  545.00},
    36: {"ticker": "ACN",   "name": "Accenture plc",                        "price":  345.00},
    37: {"ticker": "LIN",   "name": "Linde plc",                            "price":  470.00},
    38: {"ticker": "CSCO",  "name": "Cisco Systems, Inc.",                  "price":   76.00},
    39: {"ticker": "ABT",   "name": "Abbott Laboratories",                  "price":  130.00},
    40: {"ticker": "MRK",   "name": "Merck & Co., Inc.",                    "price":   95.00},
    # Large-cap (41-50)
    41: {"ticker": "AMD",   "name": "Advanced Micro Devices, Inc.",         "price":  166.47},
    42: {"ticker": "IBM",   "name": "International Business Machines Corporation", "price": 268.00},
    43: {"ticker": "INTU",  "name": "Intuit Inc.",                          "price":  645.00},
    44: {"ticker": "DHR",   "name": "Danaher Corporation",                  "price":  215.00},
    45: {"ticker": "TXN",   "name": "Texas Instruments Incorporated",       "price":  195.00},
    46: {"ticker": "NKE",   "name": "NIKE, Inc.",                           "price":   72.00},
    47: {"ticker": "QCOM",  "name": "QUALCOMM Incorporated",                "price":  165.00},
    48: {"ticker": "DIS",   "name": "The Walt Disney Company",              "price":  105.00},
    49: {"ticker": "VZ",    "name": "Verizon Communications Inc.",          "price":   44.00},
    50: {"ticker": "PFE",   "name": "Pfizer Inc.",                          "price":   28.00},
}

# ─────────────────────────── Distribution tunables ───────────────────────────

# Aggressiveness (_trade_properties): skew>1 biases towards conservative bots.
AGG_SKEW                  = 1.3
AGG_JITTER                = 0.10

# Decision interval seconds (_trade_properties): more aggressive → shorter.
INTERVAL_BASE             = 10.0
INTERVAL_SLOPE            = -7.0
INTERVAL_JITTER           = 0.15
INTERVAL_FLOOR            = 1

# Trade probability per decision (_trade_properties).
TRADE_PROB_BASE           = 0.25
TRADE_PROB_SLOPE          = 0.50
TRADE_PROB_JITTER         = 0.15

# Strategy options (_trade_properties).
STRATEGY_CHOICES          = (1, 2, 3, 4)

# Starting balance (_portfolio): log-distributed.
BALANCE_MIN               = 10_000.0
BALANCE_MAX_FACTOR        = 50.0

# Cash reserves (_portfolio): aggressive bots keep less cash.
MAX_CASH_BASE             = 0.50
MAX_CASH_SLOPE            = -0.30
MAX_CASH_JITTER           = 0.15
MIN_CASH_FRACTION_LO      = 0.30    # min_cash = max_cash * U(LO, HI)
MIN_CASH_FRACTION_HI      = 0.60

# Stocks-per-aggressiveness bands (_portfolio): low / mid / high.
STOCKS_AGG_LOW_THR        = 0.3
STOCKS_AGG_MID_THR        = 0.6
STOCKS_LOW_MIN_RANGE      = (1, 4)
STOCKS_LOW_MAX_RANGE      = (6, 12)
STOCKS_MID_MIN_RANGE      = (3, 5)
STOCKS_MID_MAX_RANGE      = (8, 15)
STOCKS_HIGH_MIN_RANGE     = (5, 8)
STOCKS_HIGH_MAX_RANGE     = (12, 20)

# Watchlist extras above max_stocks (_portfolio).
WATCHLIST_EXTRA_LO        = 3
WATCHLIST_EXTRA_HI        = 8

# Watchlist sampling weight. STOCKS is keyed by StockId in market-cap descending
# order (id 1 = largest), so 1/sid**alpha biases the watchlist towards bigger
# names: 0 = uniform, ~0.5 = mild bias, ~1.0 = strong Zipf-style.
# Kept mild — composition bias has been moved to HOLDING_WEIGHT_ALPHA so that
# big-caps still dominate by *quantity held*, but watchlists are broader.
WATCHLIST_WEIGHT_ALPHA    = 1.2

# Order types (_order_types).
USE_MARKET_BASE           = 0.10
USE_MARKET_RANGE          = 0.30
USE_MARKET_SKEW           = 1.5
USE_SLIP_BASE             = 0.50
USE_SLIP_RANGE            = 0.40
USE_SLIP_SKEW             = 0.5

# Skew for how often a bot acts out of character at an extreme-sentiment
# event. Drawn from `0.50 * skewed01(skew)` — biased toward 0, capped at
# 0.5 so a bot is never more likely to be random than in character.
EXTREME_RANDOMNESS_SKEW   = 2.0

# Cash injection (3.5): periodic nominal-growth driver. Per-bot knobs are
# seeded inverse to portfolio value at generation time, so smaller bots
# inject more often and at a higher % of portfolio. Median bot expectation:
# 5%/yr nominal. See ../KieshStockExchange/Services/BackgroundServices/Helpers/BotCashInjector.cs
# for the runtime side.
CASH_INJECTION_BASE_FREQUENCY = 0.15      # median: 15% chance / 1-hour cycle
CASH_INJECTION_BASE_AMOUNT    = 0.004     # median: 0.4% of portfolio / hit
CASH_INJECTION_SIZE_ALPHA     = 0.6       # inverse-size skew strength
CASH_INJECTION_JITTER         = 0.20      # ±20% per-bot randomness
CASH_INJECTION_FREQ_FLOOR     = 0.02
CASH_INJECTION_FREQ_CAP       = 0.45
CASH_INJECTION_AMOUNT_FLOOR   = 0.0005
CASH_INJECTION_AMOUNT_CAP     = 0.02

# Buy bias (_order_types).
BUY_BIAS_BASE             = 0.45
BUY_BIAS_SLOPE            = 0.10
BUY_BIAS_JITTER           = 0.10
BUY_BIAS_MIN              = 0.40
BUY_BIAS_MAX              = 0.60

# Slippage tolerance (_trade_limits).
SLIP_TOL_BASE             = 0.005
SLIP_TOL_SLOPE            = 0.025
SLIP_TOL_JITTER           = 0.20

# Limit offsets (_trade_limits).
MAX_LIMIT_BASE            = 0.005
MAX_LIMIT_SLOPE           = 0.015
MAX_LIMIT_JITTER          = 0.20
MIN_LIMIT_FRACTION_LO     = 0.05    # min_limit = max_limit * U(LO, HI)
MIN_LIMIT_FRACTION_HI     = 0.30
MIN_LIMIT_FLOOR           = 0.001

# Per-position max (_trade_limits): floored against 1/max_stocks downstream.
PER_POS_BASE              = 0.08
PER_POS_SLOPE             = 0.22
PER_POS_JITTER            = 0.15

# Trade amount fractions of per_pos_max (_trade_limits).
MIN_TRADE_FRACTION_LO     = 0.10
MIN_TRADE_FRACTION_HI     = 0.30
MAX_TRADE_FRACTION_LO     = 0.40
MAX_TRADE_FRACTION_HI     = 0.80

# Daily limits (_trade_limits).
MAX_DAILY_TRADES_BASE     = 500
MAX_DAILY_TRADES_SLOPE    = 2500
MAX_DAILY_TRADES_JITTER   = 0.20
MAX_DAILY_TRADES_FLOOR    = 500

MAX_OPEN_ORDERS_BASE      = 10
MAX_OPEN_ORDERS_SLOPE     = 40
MAX_OPEN_ORDERS_JITTER    = 0.20
MAX_OPEN_ORDERS_FLOOR     = 10


# ──────────────────────────── Invariant validation ───────────────────────────

# Catches misconfiguration 
def _validate() -> None:
    # Helpers — linear params evaluated at the aggressive=0 and aggressive=1
    # endpoints, since every per-bot value is interpolated between those two.
    def _both_ends(base: float, slope: float) -> tuple[float, float]:
        return base, base + slope

    def _in_unit(name: str, lo: float, hi: float) -> None:
        if not (0.0 <= lo <= 1.0) or not (0.0 <= hi <= 1.0):
            raise ValueError(f"{name} leaves [0,1] across aggressive range: lo={lo}, hi={hi}")

    def _ordered(name_lo: str, lo: float, name_hi: str, hi: float) -> None:
        if lo > hi:
            raise ValueError(f"{name_lo}={lo} must be ≤ {name_hi}={hi}")

    def _non_negative(name: str, value: float) -> None:
        if value < 0:
            raise ValueError(f"{name}={value} must be ≥ 0")

    # Fractions of portfolio / per_pos_max must stay in [0,1] at both endpoints.
    _in_unit("max_cash",   *_both_ends(MAX_CASH_BASE,   MAX_CASH_SLOPE))
    _in_unit("slip_tol",   *_both_ends(SLIP_TOL_BASE,   SLIP_TOL_SLOPE))
    _in_unit("max_limit",  *_both_ends(MAX_LIMIT_BASE,  MAX_LIMIT_SLOPE))
    _in_unit("per_pos",    *_both_ends(PER_POS_BASE,    PER_POS_SLOPE))
    _in_unit("buy_bias",   *_both_ends(BUY_BIAS_BASE,   BUY_BIAS_SLOPE))
    _in_unit("trade_prob", *_both_ends(TRADE_PROB_BASE, TRADE_PROB_SLOPE))

    # Lo/Hi paired fractions must be ordered.
    _ordered("MIN_CASH_FRACTION_LO",   MIN_CASH_FRACTION_LO,   "MIN_CASH_FRACTION_HI",   MIN_CASH_FRACTION_HI)
    _ordered("MIN_LIMIT_FRACTION_LO",  MIN_LIMIT_FRACTION_LO,  "MIN_LIMIT_FRACTION_HI",  MIN_LIMIT_FRACTION_HI)
    _ordered("MIN_TRADE_FRACTION_LO",  MIN_TRADE_FRACTION_LO,  "MIN_TRADE_FRACTION_HI",  MIN_TRADE_FRACTION_HI)
    _ordered("MAX_TRADE_FRACTION_LO",  MAX_TRADE_FRACTION_LO,  "MAX_TRADE_FRACTION_HI",  MAX_TRADE_FRACTION_HI)
    _ordered("WATCHLIST_EXTRA_LO",     WATCHLIST_EXTRA_LO,     "WATCHLIST_EXTRA_HI",     WATCHLIST_EXTRA_HI)
    _ordered("BUY_BIAS_MIN",           BUY_BIAS_MIN,           "BUY_BIAS_MAX",           BUY_BIAS_MAX)

    # Aggressiveness band thresholds must be strictly increasing and inside (0,1).
    if not (0.0 < STOCKS_AGG_LOW_THR < STOCKS_AGG_MID_THR < 1.0):
        raise ValueError(
            f"Aggressiveness thresholds must satisfy 0 < LOW({STOCKS_AGG_LOW_THR}) "
            f"< MID({STOCKS_AGG_MID_THR}) < 1"
        )

    # Stocks-per-band ranges must be ordered low→high within each tuple.
    for name, rng in [
        ("STOCKS_LOW_MIN_RANGE",  STOCKS_LOW_MIN_RANGE),  ("STOCKS_LOW_MAX_RANGE",  STOCKS_LOW_MAX_RANGE),
        ("STOCKS_MID_MIN_RANGE",  STOCKS_MID_MIN_RANGE),  ("STOCKS_MID_MAX_RANGE",  STOCKS_MID_MAX_RANGE),
        ("STOCKS_HIGH_MIN_RANGE", STOCKS_HIGH_MIN_RANGE), ("STOCKS_HIGH_MAX_RANGE", STOCKS_HIGH_MAX_RANGE),
    ]:
        if rng[0] < 1 or rng[0] > rng[1]:
            raise ValueError(f"{name}={rng} must satisfy 1 ≤ lo ≤ hi")

    # Floors must be non-negative.
    for name, value in [
        ("INTERVAL_FLOOR",         INTERVAL_FLOOR),
        ("MIN_LIMIT_FLOOR",        MIN_LIMIT_FLOOR),
        ("MAX_DAILY_TRADES_FLOOR", MAX_DAILY_TRADES_FLOOR),
        ("MAX_OPEN_ORDERS_FLOOR",  MAX_OPEN_ORDERS_FLOOR),
        ("WATCHLIST_WEIGHT_ALPHA", WATCHLIST_WEIGHT_ALPHA),
        ("CASH_INJECTION_BASE_FREQUENCY", CASH_INJECTION_BASE_FREQUENCY),
        ("CASH_INJECTION_BASE_AMOUNT",    CASH_INJECTION_BASE_AMOUNT),
        ("CASH_INJECTION_SIZE_ALPHA",     CASH_INJECTION_SIZE_ALPHA),
        ("CASH_INJECTION_JITTER",         CASH_INJECTION_JITTER),
        ("CASH_INJECTION_FREQ_FLOOR",     CASH_INJECTION_FREQ_FLOOR),
        ("CASH_INJECTION_FREQ_CAP",       CASH_INJECTION_FREQ_CAP),
        ("CASH_INJECTION_AMOUNT_FLOOR",   CASH_INJECTION_AMOUNT_FLOOR),
        ("CASH_INJECTION_AMOUNT_CAP",     CASH_INJECTION_AMOUNT_CAP),
    ]:
        _non_negative(name, value)

    # Cash injection invariants.
    if not (0.0 < CASH_INJECTION_BASE_FREQUENCY <= 1.0):
        raise ValueError(f"CASH_INJECTION_BASE_FREQUENCY={CASH_INJECTION_BASE_FREQUENCY} must be in (0, 1]")
    if not (0.0 < CASH_INJECTION_BASE_AMOUNT <= 1.0):
        raise ValueError(f"CASH_INJECTION_BASE_AMOUNT={CASH_INJECTION_BASE_AMOUNT} must be in (0, 1]")
    if not (0.0 < CASH_INJECTION_SIZE_ALPHA):
        raise ValueError(f"CASH_INJECTION_SIZE_ALPHA={CASH_INJECTION_SIZE_ALPHA} must be > 0")
    if not (0.0 < CASH_INJECTION_JITTER < 0.5):
        raise ValueError(f"CASH_INJECTION_JITTER={CASH_INJECTION_JITTER} must be in (0, 0.5)")
    _ordered("CASH_INJECTION_FREQ_FLOOR",   CASH_INJECTION_FREQ_FLOOR,
             "CASH_INJECTION_FREQ_CAP",     CASH_INJECTION_FREQ_CAP)
    _ordered("CASH_INJECTION_AMOUNT_FLOOR", CASH_INJECTION_AMOUNT_FLOOR,
             "CASH_INJECTION_AMOUNT_CAP",   CASH_INJECTION_AMOUNT_CAP)


_validate()
