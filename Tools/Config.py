# ─────────────────────────────── Stock universe ──────────────────────────────

# Ordered roughly by market cap descending (largest first). All "price" values
# are USD references — Person.py converts to EUR for EUR-home bots, and the
# Listings sheet writer in GenerateAIUsers.py derives the EUR seed price for
# cross-listed stocks via FX_BASE_RATES.
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
    10: {"ticker": "NESN",  "name": "Nestle S.A.",                          "price":  102.50},
    # Mega-cap mixed (11-20)
    11: {"ticker": "LLY",   "name": "Eli Lilly & Co",                       "price":  812.69},
    12: {"ticker": "WMT",   "name": "Walmart Inc.",                         "price":   97.47},
    13: {"ticker": "JPM",   "name": "JPMorgan Chase & Co.",                 "price":  285.00},
    14: {"ticker": "V",     "name": "Visa Inc.",                            "price":  357.04},
    15: {"ticker": "ORCL",  "name": "Oracle Corporation",                   "price":  245.12},
    16: {"ticker": "MA",    "name": "Mastercard Incorporated",              "price":  568.22},
    17: {"ticker": "XOM",   "name": "Exxon Mobil Corporation",              "price":  115.00},
    18: {"ticker": "UNH",   "name": "UnitedHealth Group Incorporated",      "price":  580.00},
    # Slot 19: European-domiciled (LVMH) replaces JNJ.
    19: {"ticker": "LVMH",  "name": "LVMH Moet Hennessy Louis Vuitton SE",  "price":  790.00},
    20: {"ticker": "COST",  "name": "Costco Wholesale Corporation",         "price":  950.00},
    # Large-cap (21-30)
    21: {"ticker": "NFLX",  "name": "Netflix, Inc.",                        "price": 1180.49},
    22: {"ticker": "PG",    "name": "The Procter & Gamble Company",         "price":  168.00},
    23: {"ticker": "HD",    "name": "The Home Depot, Inc.",                 "price":  410.00},
    24: {"ticker": "BAC",   "name": "Bank of America Corporation",          "price":   48.45},
    25: {"ticker": "ABBV",  "name": "AbbVie Inc.",                          "price":  215.00},
    26: {"ticker": "CRM",   "name": "Salesforce, Inc.",                     "price":  305.00},
    # Slot 27: ASML (already European-domiciled, becomes EUR-only).
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
    # Slot 37: Linde plc reclassified as EUR-only for this simulation.
    37: {"ticker": "LIN",   "name": "Linde plc",                            "price":  470.00},
    38: {"ticker": "CSCO",  "name": "Cisco Systems, Inc.",                  "price":   76.00},
    39: {"ticker": "ABT",   "name": "Abbott Laboratories",                  "price":  130.00},
    40: {"ticker": "MRK",   "name": "Merck & Co., Inc.",                    "price":   95.00},
    # Large-cap (41-50)
    41: {"ticker": "AMD",   "name": "Advanced Micro Devices, Inc.",         "price":  166.47},
    42: {"ticker": "IBM",   "name": "International Business Machines Corporation", "price": 268.00},
    43: {"ticker": "INTU",  "name": "Intuit Inc.",                          "price":  645.00},
    # Slot 44: European-domiciled (NOVO) replaces DHR.
    44: {"ticker": "NOVO",  "name": "Novo Nordisk A/S",                     "price":   78.00},
    45: {"ticker": "TXN",   "name": "Texas Instruments Incorporated",       "price":  195.00},
    # Slot 46: European-domiciled (SAP) replaces NKE.
    46: {"ticker": "SAP",   "name": "SAP SE",                               "price":  245.00},
    # Slot 47: European-domiciled (OR) replaces QCOM.
    47: {"ticker": "OR",    "name": "L'Oreal S.A.",                         "price":  385.00},
    # Slot 48: European-domiciled (SIE) replaces DIS.
    48: {"ticker": "SIE",   "name": "Siemens AG",                           "price":  205.00},
    # Slot 49: European-domiciled (AZN) replaces VZ.
    49: {"ticker": "AZN",   "name": "AstraZeneca PLC",                      "price":   72.00},
    # Slot 50: European-domiciled (ALV) replaces PFE.
    50: {"ticker": "ALV",   "name": "Allianz SE",                           "price":  365.00},
}

# ────────────────────────── Multi-currency tunables ──────────────────────────

# Supported currencies for trading + Funds.
SUPPORTED_CURRENCIES = ["USD", "EUR"]

# FX base rates. Key is "FROM/TO" reading "1 FROM = X TO".
FX_BASE_RATES = {
    "EUR/USD": 1.08,
}

# Per-bot home-currency draw weights. Must sum to 1.
HOME_CURRENCY_WEIGHTS = {
    "USD": 0.70,
    "EUR": 0.30,
}

# Stocks that trade on both USD and EUR books. Cap-diverse — 5 each from
# id ranges 1-10, 11-20, 21-30, 31-50.
CROSS_LISTED_STOCK_IDS = [
    1, 3, 4, 5, 6, 7, 8, 9, 14, 16, 20,
    23, 25, 28, 32, 33, 36, 38, 42, 45,
]

# Stocks that trade only on the EUR book.
EUR_ONLY_STOCK_IDS = [10, 19, 27, 37, 44, 46, 47, 48, 49, 50]

# Random jitter applied to the derived EUR seed price for cross-listed stocks.
LISTING_PRICE_JITTER = 0.005   # ±0.5%

# FX drift tunables (documentation; the runtime values live in C# constants
# inside FxRateService — keep in sync with this block).
FX_TICK_INTERVAL_SECONDS = 60
FX_AR1_ALPHA             = 0.92
FX_AR1_AMPLITUDE         = 0.005
FX_CONVERT_SPREAD        = 0.001   # ±0.1% around mid = 0.2% spread
FX_RATE_BAND             = 0.20    # mid clamped to base ±20%

# ───────────────────── §3.7 Arbitrage cohort + platform house account ─────────────────────
# A small fixed cohort of AiStrategy.Arbitrage (=5) bots, generated SEPARATELY from the random
# fleet (STRATEGY_CHOICES stays 0-4 so the general draw never produces one). They keep each
# cross-listed stock's USD/EUR books coupled, self-fund (no cash injection), seed dual-currency,
# and pay the FX conversion spread to the house account.
ARBITRAGE_COHORT_SIZE     = 5

# Per-bot draw ranges (jittered so the cohort isn't identical). MinArbitrageRate ≥ FX_CONVERT_SPREAD
# so an acted trade clears the round-trip spread; inventory + cadence bound the held risk.
ARB_MIN_RATE_RANGE        = (0.0015, 0.0030)   # MinArbitrageRatePrc (≈ 1.5–3× the spread)
ARB_MAX_INVENTORY_RANGE   = (200, 800)         # MaxInventoryPerStock (shares)
ARB_CONVERSION_CADENCE    = (180, 420)         # ConversionCadenceSeconds (3–7 min)
ARB_DECISION_INTERVAL     = (2, 5)             # seconds between arbitrage decisions
# Dual-currency seed: USD leg (home) + EUR leg (secondary). Large + balanced so the cohort can
# arbitrage both directions without starving a book; the house is seeded larger still (below).
ARB_SEED_BALANCE_USD      = 2_000_000.0
ARB_SEED_BALANCE_EUR      = 1_800_000.0

# ───────────────────── §mm-cohort: all-weather market-maker cohort ─────────────────────
# A small fixed cohort of AiStrategy.MarketMakerHouse (=6) bots, generated SEPARATELY from the
# random fleet (STRATEGY_CHOICES stays 0-4). They continuously post two-sided resting LIMIT quotes
# around a one-sided-book-surviving reference, self-fund (no cash injection), and cover the whole
# board in their home currency. DEFAULT SIZE 0 ⇒ no strategy-6 bots are seeded ⇒ byte-identical to
# today; set > 0 to seed the cohort for an MM A/B bake (then toggle Bots:MarketMaker:Enabled).
MARKET_MAKER_COHORT_SIZE  = 0

# Per-bot draw ranges (jittered so the cohort isn't identical). MaxInventoryPerStock IS the hard
# two-sided position cap the quote math clamps against; the seed balance funds bids + §F14 short
# collateral. Single home currency (USD), flat initial holdings, fast refresh cadence.
MM_MAX_INVENTORY_RANGE    = (200, 800)         # MaxInventoryPerStock (shares), the |inventory| cap
MM_DECISION_INTERVAL      = (1, 2)             # seconds between quote refreshes
MM_SEED_BALANCE_USD       = 5_000_000.0

# The platform house account: reserved UserId (server reads Platform:HouseUserId, default 20002),
# Identity + dual-currency Holding, NO Profile (so it is never a bot / never in the fleet). Seeded
# generously in BOTH currencies so it always has inventory to settle conversions (a depleted house
# fails the convert rather than minting/destroying — see UserPortfolioService.ConvertInternalAsync).
HOUSE_USER_ID_OFFSET      = 2                  # UserId = NUM_PEOPLE + 2 (admin is +1)
HOUSE_SEED_BALANCE_USD    = 50_000_000.0
HOUSE_SEED_BALANCE_EUR    = 45_000_000.0

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
# Ids match C# AiStrategy: 0=MarketMaker, 1=TrendFollower, 2=MeanReversion, 3=Random, 4=Scalper.
# §P6: MarketMaker (0) included so a slice of bots quote tight two-sided and keep the touch liquid.
STRATEGY_CHOICES          = (0, 1, 2, 3, 4)

# Sentiment-dynamics §: NON-EVEN strategy ratios so momentum can build a trend while reverters + the value
# anchor reliably end it (loop gain G≈1). Net follow-leaning during a move, reversion-heavy at extremes.
# Keys are the AiStrategy ids above; weights must sum to 1. Arbitrage (5) stays out (separate cohort).
STRATEGY_WEIGHTS = {
    0: 0.13,   # MarketMaker — liquidity floor
    1: 0.35,   # TrendFollower — builds + (via high-lateness tail) tops the trend
    2: 0.20,   # MeanReversion — ends the boom
    3: 0.20,   # Random — entropy / liquidity
    4: 0.12,   # Scalper — fast momentum, leads, adds turnover
}

# Sentiment-dynamics §: per-bot lateness L ∈ [0,1] draw. skewed01(skew>1) biases toward 0, so most momentum
# bots are EARLY (follow the slope) with a ~10–15% high-L FOMO tail that chases the level and tops the trend.
LATENESS_SKEW             = 2.2

# ───────────────────── Advanced-order probabilities (per bot, per tick) ───────────────────────
# §3.6 P6: each bot is assigned its own probability of choosing each advanced order kind, drawn
# uniformly from a per-strategy (lo, hi) range so behaviour matches the strategy. These feed the
# Profile sheet -> server AIUser.*Prob -> AiBotDecisionService. Tuned HIGH (well above the ~10%
# real-world figure) on purpose: more shorts + stop cascades make the chart genuinely chaotic
# instead of drifting on a gentle slope. Sum per bot ≈ how often a tick yields an advanced order.
# Keys: stop / trailing / short / long_bracket / short_bracket. Adjust freely + re-run the generator.
ADVANCED_PROFILES = {
    # MarketMaker — provides liquidity; small but non-zero so it still adds noise.
    0: {"stop": (0.01, 0.03), "trailing": (0.005, 0.02), "short": (0.01, 0.03),
        "long_bracket": (0.005, 0.02), "short_bracket": (0.005, 0.02)},
    # TrendFollower — rides trends: heavy trailing stops, moderate stops/brackets.
    1: {"stop": (0.03, 0.07), "trailing": (0.05, 0.12), "short": (0.02, 0.05),
        "long_bracket": (0.02, 0.06), "short_bracket": (0.01, 0.03)},
    # MeanReversion — bets on reversals: brackets + shorts heavy.
    2: {"stop": (0.02, 0.05), "trailing": (0.01, 0.03), "short": (0.04, 0.08),
        "long_bracket": (0.04, 0.10), "short_bracket": (0.03, 0.07)},
    # Random — the chaos engine: a strong mix of everything.
    3: {"stop": (0.04, 0.09), "trailing": (0.03, 0.08), "short": (0.04, 0.09),
        "long_bracket": (0.03, 0.08), "short_bracket": (0.03, 0.08)},
    # Scalper — quick risk control: stop-heavy, some shorts, light brackets.
    4: {"stop": (0.05, 0.11), "trailing": (0.03, 0.07), "short": (0.03, 0.06),
        "long_bracket": (0.02, 0.05), "short_bracket": (0.02, 0.05)},
}

# Starting balance (_portfolio): log-distributed. 10x larger so small order
# fractions still clear ≥1 share on high-priced stocks and deepen the book.
BALANCE_MIN               = 1_000_000.0
BALANCE_MAX_FACTOR        = 10.0

# Cash reserves (_portfolio): aggressive bots keep less cash.
# Seed cash% = (min_cash + max_cash) / 2 (band midpoint, see Person.py). The base is lowered from 0.50 so
# that midpoint lands back at ~0.23 — preserving the historical seed holdings while the band stays centered
# on the seed (the cash-homeostasis rest-point). Soak-calibration knob: tune so the portfolio-weighted
# (Min+Max)/2 matches the measured seed cash%.
MAX_CASH_BASE             = 0.45
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
USE_MARKET_BASE           = 0.20
USE_MARKET_RANGE          = 0.30
USE_MARKET_SKEW           = 1.5
USE_SLIP_BASE             = 0.50
USE_SLIP_RANGE            = 0.40
USE_SLIP_SKEW             = 0.5

# Skew for how often a bot acts out of character at an extreme-sentiment
# event. Drawn from `0.50 * skewed01(skew)` — biased toward 0, capped at
# 0.5 so a bot is never more likely to be random than in character.
EXTREME_RANDOMNESS_SKEW   = 2.0

# Cash-injection knobs. Seeded inverse to portfolio value, so smaller bots inject MORE often and at a
# HIGHER % — tuned for a wide, visible spread (some bots inject a lot, some a little). Amount cap stays
# within the C# validator bound (AIUser.CashInjectionAmountPrc ≤ 0.05); frequency cap stays ≤ 0.50.
CASH_INJECTION_BASE_FREQUENCY = 0.25      # median: 25% chance / 1-hour cycle
CASH_INJECTION_BASE_AMOUNT    = 0.009     # median: 0.9% of portfolio / hit
CASH_INJECTION_SIZE_ALPHA     = 0.6       # inverse-size skew strength
CASH_INJECTION_JITTER         = 0.25      # ±25% per-bot randomness
CASH_INJECTION_FREQ_FLOOR     = 0.05      # every bot injects at least sometimes
CASH_INJECTION_FREQ_CAP       = 0.50      # most-active bots: 50% hourly chance
CASH_INJECTION_AMOUNT_FLOOR   = 0.001
CASH_INJECTION_AMOUNT_CAP     = 0.04      # biggest hits: 4% of portfolio

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

# Limit offsets (_trade_limits). Tight so resting orders cluster near market and fill.
# §P6 tightness: distances baked directly here (the old runtime DecisionDistanceMult=0.32 dial folded in)
# so the generated per-bot values ARE the production geometry — no runtime multiplier. Far walls top out
# ~8%, the whole ladder rides near the touch (validated: median drift ~5.3%, lively tail, 0 escapes).
MAX_LIMIT_BASE            = 0.001
MAX_LIMIT_SLOPE           = 0.0016
MAX_LIMIT_JITTER          = 0.20
MIN_LIMIT_FRACTION_LO     = 0.05    # min_limit = max_limit * U(LO, HI)
MIN_LIMIT_FRACTION_HI     = 0.30
MIN_LIMIT_FLOOR           = 0.0003

# §P6 tiered limit ladder (_tiers). Close = the existing Min/MaxLimitOffsetPrc (tight, near the touch).
# Mid + Far are standing walls further out; a fired (slippage-capped) stop runs into the Far walls and is
# absorbed. Each (lo,hi) is the per-bot uniform draw range; Person.py enforces ordering
# (Close ≤ Mid ≤ Far) and StopDistanceMax < FarLimitMin so a stop never sits outside the walls.
MID_LIMIT_MIN_RANGE       = (0.003, 0.006)   # MidLimitMinPrc
MID_LIMIT_MAX_RANGE       = (0.010, 0.016)   # MidLimitMaxPrc
FAR_LIMIT_MIN_RANGE       = (0.019, 0.032)   # FarLimitMinPrc
FAR_LIMIT_MAX_RANGE       = (0.048, 0.080)   # FarLimitMaxPrc (Far walls cap ~8%)
# Protective-stop distance band. Max is additionally clamped < FarLimitMin in Person.py.
STOP_DISTANCE_MAX_RANGE   = (0.010, 0.016)   # StopDistanceMaxPrc (pre-clamp)
STOP_DISTANCE_MIN_FRACTION = (0.50, 0.90)    # StopDistanceMinPrc = StopDistanceMaxPrc * U(lo,hi)
# §P6: per-bot take-profit band — was the global Advanced:TpOffsetPrc (3-8%), now promoted to per-bot
# and baked tight (×0.32). The two bracket TP legs are drawn from each bot's [TpOffsetMin, TpOffsetMax].
TP_OFFSET_MIN_RANGE       = (0.010, 0.014)   # TpOffsetMinPrc
TP_OFFSET_MAX_RANGE       = (0.018, 0.026)   # TpOffsetMaxPrc
# Total resting Far-order value the tier-aware prune allows, as a fraction of portfolio.
FAR_BUDGET_RANGE          = (0.05, 0.15)     # FarBudgetPrc

# Round 2 §0012 (extension E5): per-bot preference for round-trip vs flip on a Path-2 bracket
# entry. 1.0 = always size to ≤ |inventory| (always round-trip); 0.0 = always size past
# inventory (always flip); 0.5 = neutral. Per-strategy point values from the round-1 plan
# §3 E5 with light jitter (small bot-to-bot variation around the strategy's central tendency).
# Keys: 0 MarketMaker, 1 TrendFollower, 2 MeanReversion, 3 Random, 4 Scalper.
ROUNDTRIP_BIAS_PER_STRATEGY = {
    0: 0.5,   # MarketMaker — symmetric
    1: 0.2,   # TrendFollower — prefers flip (trend bets)
    2: 0.8,   # MeanReversion — prefers round-trip (reversion thesis)
    3: 0.5,   # Random
    4: 0.7,   # Scalper — quick round-trips, occasional flip
}
ROUNDTRIP_BIAS_JITTER     = 0.10   # ± uniform on each draw

# Per-position max (_trade_limits): floored against 1/max_stocks downstream.
PER_POS_BASE              = 0.08
PER_POS_SLOPE             = 0.22
PER_POS_JITTER            = 0.15

# Trade amount fractions of per_pos_max (_trade_limits). Small so each order is
# ~0.1-1% of portfolio and many small orders form a granular ladder, not paywalls.
MIN_TRADE_FRACTION_LO     = 0.005
MIN_TRADE_FRACTION_HI     = 0.015
MAX_TRADE_FRACTION_LO     = 0.025
MAX_TRADE_FRACTION_HI     = 0.05

# Daily limits (_trade_limits).
MAX_DAILY_TRADES_BASE     = 500
MAX_DAILY_TRADES_SLOPE    = 2500
MAX_DAILY_TRADES_JITTER   = 0.20
MAX_DAILY_TRADES_FLOOR    = 500

# More resting rungs per bot to keep the book deep now that each order is small.
MAX_OPEN_ORDERS_BASE      = 50
MAX_OPEN_ORDERS_SLOPE     = 100
MAX_OPEN_ORDERS_JITTER    = 0.20
MAX_OPEN_ORDERS_FLOOR     = 50


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

    # §P6 tiered ladder: each draw range must be inside [0,1] and lo ≤ hi; and the tier bands must not
    # overlap (Mid < Far) with the protective stop strictly inside the Far walls (StopMax < FarMin).
    for name, rng in [
        ("MID_LIMIT_MIN_RANGE", MID_LIMIT_MIN_RANGE), ("MID_LIMIT_MAX_RANGE", MID_LIMIT_MAX_RANGE),
        ("FAR_LIMIT_MIN_RANGE", FAR_LIMIT_MIN_RANGE), ("FAR_LIMIT_MAX_RANGE", FAR_LIMIT_MAX_RANGE),
        ("STOP_DISTANCE_MAX_RANGE", STOP_DISTANCE_MAX_RANGE), ("FAR_BUDGET_RANGE", FAR_BUDGET_RANGE),
        ("STOP_DISTANCE_MIN_FRACTION", STOP_DISTANCE_MIN_FRACTION),
        ("TP_OFFSET_MIN_RANGE", TP_OFFSET_MIN_RANGE), ("TP_OFFSET_MAX_RANGE", TP_OFFSET_MAX_RANGE),
    ]:
        _in_unit(name, rng[0], rng[1])
        _ordered(f"{name}[0]", rng[0], f"{name}[1]", rng[1])
    if MID_LIMIT_MAX_RANGE[1] > FAR_LIMIT_MIN_RANGE[0]:
        raise ValueError("MID_LIMIT_MAX_RANGE must stay below FAR_LIMIT_MIN_RANGE (tiers must not overlap)")
    if STOP_DISTANCE_MAX_RANGE[1] > FAR_LIMIT_MIN_RANGE[0]:
        raise ValueError("STOP_DISTANCE_MAX_RANGE must stay below FAR_LIMIT_MIN_RANGE (stop inside Far walls)")
    if TP_OFFSET_MIN_RANGE[1] > TP_OFFSET_MAX_RANGE[0]:
        raise ValueError("TP_OFFSET_MIN_RANGE must stay below TP_OFFSET_MAX_RANGE (per-bot TpMin ≤ TpMax)")

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

    # Multi-currency invariants.
    if not SUPPORTED_CURRENCIES:
        raise ValueError("SUPPORTED_CURRENCIES must not be empty")
    total_weight = sum(HOME_CURRENCY_WEIGHTS.values())
    if abs(total_weight - 1.0) > 1e-6:
        raise ValueError(
            f"HOME_CURRENCY_WEIGHTS must sum to 1 (got {total_weight}).")
    for ccy in HOME_CURRENCY_WEIGHTS:
        if ccy not in SUPPORTED_CURRENCIES:
            raise ValueError(
                f"HOME_CURRENCY_WEIGHTS key {ccy} not in SUPPORTED_CURRENCIES.")
    for pair in FX_BASE_RATES:
        a, _, b = pair.partition("/")
        if a not in SUPPORTED_CURRENCIES or b not in SUPPORTED_CURRENCIES:
            raise ValueError(
                f"FX_BASE_RATES pair {pair} references unsupported currency.")
    overlap = set(CROSS_LISTED_STOCK_IDS) & set(EUR_ONLY_STOCK_IDS)
    if overlap:
        raise ValueError(
            f"CROSS_LISTED_STOCK_IDS and EUR_ONLY_STOCK_IDS overlap: {sorted(overlap)}.")
    if not (0.0 <= LISTING_PRICE_JITTER < 0.5):
        raise ValueError(
            f"LISTING_PRICE_JITTER={LISTING_PRICE_JITTER} must be in [0, 0.5).")

    # §3.7 arbitrage cohort + house invariants.
    if ARBITRAGE_COHORT_SIZE < 0:
        raise ValueError(f"ARBITRAGE_COHORT_SIZE={ARBITRAGE_COHORT_SIZE} must be ≥ 0")
    if 5 in STRATEGY_CHOICES:
        raise ValueError("STRATEGY_CHOICES must NOT include 5 (Arbitrage) — the cohort is generated separately.")
    # §mm-cohort: the market-maker cohort (strategy 6) is also generated separately from the random fleet.
    if MARKET_MAKER_COHORT_SIZE < 0:
        raise ValueError(f"MARKET_MAKER_COHORT_SIZE={MARKET_MAKER_COHORT_SIZE} must be ≥ 0")
    if 6 in STRATEGY_CHOICES:
        raise ValueError("STRATEGY_CHOICES must NOT include 6 (MarketMakerHouse) — the cohort is generated separately.")

    # Sentiment-dynamics §: strategy weights must sum to 1, key only the non-arbitrage strategies, and the
    # lateness skew must be positive.
    sw_total = sum(STRATEGY_WEIGHTS.values())
    if abs(sw_total - 1.0) > 1e-6:
        raise ValueError(f"STRATEGY_WEIGHTS must sum to 1 (got {sw_total}).")
    for sid in STRATEGY_WEIGHTS:
        if sid not in (0, 1, 2, 3, 4):
            raise ValueError(f"STRATEGY_WEIGHTS key {sid} must be a non-arbitrage strategy id (0–4).")
    _non_negative("LATENESS_SKEW", LATENESS_SKEW)
    if LATENESS_SKEW <= 0:
        raise ValueError(f"LATENESS_SKEW={LATENESS_SKEW} must be > 0.")
    _ordered("ARB_MIN_RATE_RANGE[0]", ARB_MIN_RATE_RANGE[0], "ARB_MIN_RATE_RANGE[1]", ARB_MIN_RATE_RANGE[1])
    _in_unit("ARB_MIN_RATE_RANGE", ARB_MIN_RATE_RANGE[0], ARB_MIN_RATE_RANGE[1])
    if ARB_MIN_RATE_RANGE[0] < FX_CONVERT_SPREAD:
        raise ValueError(
            f"ARB_MIN_RATE_RANGE[0]={ARB_MIN_RATE_RANGE[0]} must be ≥ FX_CONVERT_SPREAD={FX_CONVERT_SPREAD} "
            f"(else an acted trade need not clear the round-trip spread).")
    for name, rng in [("ARB_MAX_INVENTORY_RANGE", ARB_MAX_INVENTORY_RANGE),
                      ("ARB_CONVERSION_CADENCE", ARB_CONVERSION_CADENCE),
                      ("ARB_DECISION_INTERVAL", ARB_DECISION_INTERVAL)]:
        if rng[0] < 1 or rng[0] > rng[1]:
            raise ValueError(f"{name}={rng} must satisfy 1 ≤ lo ≤ hi")
    for name, value in [("ARB_SEED_BALANCE_USD", ARB_SEED_BALANCE_USD),
                        ("ARB_SEED_BALANCE_EUR", ARB_SEED_BALANCE_EUR),
                        ("HOUSE_SEED_BALANCE_USD", HOUSE_SEED_BALANCE_USD),
                        ("HOUSE_SEED_BALANCE_EUR", HOUSE_SEED_BALANCE_EUR)]:
        if value <= 0:
            raise ValueError(f"{name}={value} must be > 0")


_validate()
