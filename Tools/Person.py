from faker import Faker
import random
import re

# Fake data generator instance
fake = Faker()
USERNAME_RE = re.compile(r"^[a-zA-Z0-9]{5,20}$")

# Stock data
TICKERS = [
    "MSFT", "NVDA", "AAPL", "AMZN", "GOOG", "META", "AVGO", "TSLA", "TSM", "WMT",
    "LLY", "V", "ORCL", "NFLX", "MA", "BAC", "ASML", "KO", "BABA", "MCD", "AMD"
]

# Corresponding stock prices
PRICES = dict(zip(TICKERS, [
    513.71, 173.50, 213.88, 231.44, 194.08, 712.68, 290.18, 316.06, 245.6, 97.47, 812.69, 
    357.04, 245.12, 1180.49, 568.22, 48.45, 711.25, 69.17, 120.03, 298.47, 166.47
]))

# Mapping from ticker to stock ID (1-based index)
STOCKIDS = dict(zip(TICKERS, range(1, len(TICKERS)+1)))

# Mapping to Company name
COMPANY_NAMES = dict(zip(TICKERS, [
    "Microsoft Corporation", "NVIDIA Corporation", "Apple Inc.", "Amazon.com, Inc.", 
    "Alphabet Inc.", "Meta Platforms, Inc.", "Broadcom Inc.", "Tesla, Inc.", 
    "Taiwan Semiconductor Manufacturing", "Walmart Inc.", 
    "Eli Lilly & Co", "Visa Inc.", "Oracle Corporation", 
    "Netflix, Inc.", "Mastercard Incorporated", "Bank of America Corporation", 
    "ASML Holding N.V.", "The Coca-Cola Company", "Alibaba Group Holding Limited", 
    "McDonald's Corporation", "Advanced Micro Devices, Inc."
]))

def generate_username():
    while True:
        raw = fake.user_name()
        cleaned = "".join(ch for ch in raw if ch.isalnum())
        if len(cleaned) < 5: continue
        cleaned = cleaned[:20]  # Trim to max 20 chars
        if USERNAME_RE.match(cleaned) and cleaned not in Person.usernames: 
            Person.usernames.add(cleaned)
            return cleaned

def clamp01(x: float) -> float:
    """Clamp a float into [0, 1]."""
    return 0.0 if x < 0.0 else 1.0 if x > 1.0 else x

def skewed01(skew: float) -> float:
    """
    Return a value in [0,1] with a simple power-law skew.
    skew > 1  → values biased towards 0
    skew = 1  → uniform
    0 < skew < 1 → values biased towards 1
    """
    u = random.random()
    return u ** skew

def jitter(x: float, rel: float = 0.1) -> float:
    """
    Multiply x by a random factor in [1-rel, 1+rel].
    Used to add mild randomness around a base value.
    """
    factor = 1.0 + random.uniform(-rel, rel)
    return x * factor

def sample_log_balance(min_base: float = 5_000.0, max_factor: float = 100.0) -> int:
    """Sample a 'log-like' balance:
    balance = min_base * (max_factor ** u),  u ~ U(0,1)"""
    u = random.random()
    return int(min_base * (max_factor ** u))

class Person:
    idx = 1  # Class variable to assign unique user IDs
    usernames = set()  # Class variable to track used usernames
    def __init__(self):
        # Each AIUser gets a fixed random seed so behaviour can be reproducible
        self.seed = random.randint(1_000_000, 10_000_000)
        self.Identity()         # Full name, username, email, birthdate
        self.TradeProperties()  # Aggressiveness, online probability, decision interval, trade probability
        self.Portfolio()        # Balance, cash reserves, min/max open positions, watchlist, holdings
        self.OrderTypes()       # Probabilities for market/slippage orders, buy bias
        self.TradeLimits()      # Slippage tolerance, limit offsets, per-position max, min/max trade amounts, daily limits

    def Identity(self):
        self.user_id   = Person.idx
        Person.idx    += 1                      # Increment for next person
        self.full_name = fake.name()            # Generate full name
        self.username  = generate_username()    # Generate valid username
        self.email     = f"{self.username}@{fake.free_email_domain()}"
        self.birthdate = fake.date_of_birth(minimum_age=18, maximum_age=80) 

    def TradeProperties(self):
        # Slight bias towards lower aggressiveness (more conservative bots).
        base_agg         = skewed01(skew=1.3)
        self.aggressive  = clamp01(jitter(base_agg, rel=0.10))      # 0–1
        self.AggressivenessPrc = self.aggressive                    # alias

        # Probability of being online at any given time.
        base_online      = 0.2 + 0.8 * skewed01(skew=0.7)
        self.online_prob = clamp01(jitter(base_online, rel=0.10))

        # More aggressive → shorter interval.
        base_interval = 20.0 - 12.0 * self.aggressive * self.aggressive         # 8–20 seconds
        self.interval_seconds = int(max(1, jitter(base_interval, rel=0.15))) 

        # Probability to trade each decision
        base_trade_prob = 0.10 + 0.5 * self.aggressive * self.aggressive        # ~10–60%
        self.trade_prob = clamp01(jitter(base_trade_prob, rel=0.15))
        
        # Strategy: fixed for now, could vary based on aggressiveness later
        self.strategy = random.choice([1, 2, 3, 4])

    def Portfolio(self):
        # Starting balance: log-distributed from $10,000 to ~$500,000
        self.balance = sample_log_balance(min_base=10_000.0, max_factor=50.0)

        # Cash reserves: Aggressive bots keep less cash.
        base_max_cash = 0.50 - 0.30 * self.aggressive                   # ~20–50%
        self.max_cash = clamp01(jitter(base_max_cash, rel=0.15))        # 15% jitter
        self.min_cash = self.max_cash * random.uniform(0.30, 0.60)      # 30–60% of max cash

        # Min/Max open positions: more aggressive bots hold more stocks.
        if self.aggressive < 0.3:
            self.min_stocks = random.randint(1, 4)
            self.max_stocks = random.randint(6, 12)
        elif self.aggressive < 0.6:
            self.min_stocks = random.randint(3, 5)
            self.max_stocks = random.randint(8, 15)
        else:
            self.min_stocks = random.randint(5, 8)
            self.max_stocks = random.randint(12, 20)

        # Watchlist is a bit larger than what they actually hold
        watchlist_extra = random.randint(3, 8)
        watchlist_size = min(len(TICKERS), self.max_stocks + watchlist_extra)

        # Pick random stocks for the watchlist
        watchlist = random.sample(TICKERS, watchlist_size)
        watch_ids = sorted(STOCKIDS[s] for s in watchlist)
        self.watchList_csv = ",".join(str(id) for id in watch_ids) 

        # Select actual portfolio from watchlist
        n_stocks = random.randint(self.min_stocks, self.max_stocks)
        portfolio = sorted(random.sample(watchlist, n_stocks))                                      # Actual held stocks
        amount_per_stock = self.balance * (1 - (self.min_cash*2 + self.max_cash)/3) // n_stocks     # Equal allocation 
        self.holdings = { s: int(amount_per_stock // PRICES[s]) for s in portfolio }                # Get number of shares

    def OrderTypes(self):
        # Probabilities for order types
        self.use_market  = 0.05 + 0.20 * skewed01(skew=1.5)                 # 5–25%
        self.use_slip   = 0.50 + 0.40 * skewed01(skew=0.5)                  # 40–90%

        # Buy bias: slightly >50% buys for aggressive bots
        base_buy_bias  = 0.50 + 0.10 * (self.aggressive - 0.5)              # ~45–55%
        buy_bias       = clamp01(jitter(base_buy_bias, rel=0.10))
        self.buy_bias  = max(0.40, min(0.60, buy_bias))                     # Clamp to 40–60%

    def TradeLimits(self):
        # Slippage tolerance: more aggressive bots accept higher slippage.
        base_slip_tol = 0.005 + 0.025 * self.aggressive                     # 0.5–3%
        self.slippage_tolerance = clamp01(jitter(base_slip_tol, rel=0.20))  

        # Limit offsets: more aggressive bots use wider limits.
        base_max_limit = 0.02 + 0.03 * self.aggressive                      # 2–5%
        max_limit      = clamp01(jitter(base_max_limit, rel=0.20))
        min_limit      = max_limit * random.uniform(0.02, 0.30)             # 2–30% of max limit
        self.min_limit_offset = clamp01(min_limit)
        self.max_limit_offset = clamp01(max_limit)

        # Max position size as fraction of portfolio: more aggressive bots allow larger positions.
        base_per_pos = 0.08 + 0.22 * self.aggressive
        portfolio_max = 1.0 / self.max_stocks
        per_pos_max  = clamp01(jitter(base_per_pos, rel=0.15))
        self.per_pos_max = min(per_pos_max, portfolio_max)

        # Min/Max trade amounts as fractions of total portfolio.
        self.min_trade_amount = self.per_pos_max * random.uniform(0.10, 0.30)   # 10–30% of per_pos_max
        self.max_trade_amount = self.per_pos_max * random.uniform(0.40, 0.80)   # 40–80% of per_pos_max

        # Daily limits: more aggressive bots trade more often.
        base_trade = int(100 + 300 * self.aggressive)                           # 100-400 trades
        self.max_daily_trades   = max(100, int(jitter(base_trade, rel=0.20)))   # at least 100   
        base_open_orders = int(10 + 40 * self.aggressive)                    # 10-50 open orders
        self.max_orders  = max(10, int(jitter(base_open_orders, rel=0.20)))  # at least 10

    def ToIdentityList(self):
        return [
            self.user_id,
            self.username,
            self.full_name,
            self.email,
            self.birthdate
        ]

    def ToProfileList(self):
        return [
            self.user_id,                                   # int: user id
            self.seed,                                      # int: RNG seed
            self.interval_seconds,                          # int: decision interval (seconds)
            round(self.trade_prob, 4),                      # float: trade probability
            round(self.use_market, 4),                      # float: probability of market orders
            round(self.use_slip, 4),                        # float: probability of slippage orders
            round(self.online_prob, 4),                     # float: online probability
            round(self.buy_bias, 4),                        # float: buy bias
            round(self.min_trade_amount, 4),                # float: min trade amount (fraction)
            round(self.max_trade_amount, 4),                # float: max trade amount (fraction)
            round(self.per_pos_max, 4),                     # float: per-position max (fraction)
            round(self.min_cash, 4),                        # float: min cash (fraction)
            round(self.max_cash, 4),                        # float: max cash (fraction)
            round(self.slippage_tolerance, 4),              # float: slippage tolerance
            round(self.min_limit_offset, 4),                # float: min limit offset
            round(self.max_limit_offset, 4),                # float: max limit offset
            round(self.aggressive, 4),                      # float: aggressiveness
            self.min_stocks,                                # int: minimum stocks held
            self.max_stocks,                                # int: maximum stocks held
            self.max_daily_trades,                          # int: max daily trades
            self.max_orders,                                # int: max open orders
            self.watchList_csv,                             # str: watchlist CSV of stock IDs
            self.strategy                                   # int: strategy id
        ]

    def ToHoldingList(self):
        result = [self.user_id, round(self.balance, 2)]
        for ticker in TICKERS:
            if ticker in self.holdings:
                result.append(self.holdings[ticker])
            else:
                result.append(0)
        return result

    def __repr__(self):
        return (f"Person({self.username!r}, Aggressive={self.aggressive:.2f}, balance=${self.balance:.2f})")