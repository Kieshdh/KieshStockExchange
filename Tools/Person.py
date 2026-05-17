from faker import Faker
import random
import re

# This holds all the tunables for the data used in this class.
from Config import *

# Fake data generator instance
fake = Faker()
USERNAME_RE = re.compile(r"^[a-zA-Z0-9]{5,20}$")

def generate_username():
    # Cap attempts so the loop fails if there are no new usernames available
    attempts = 1000
    for _ in range(attempts):
        raw = fake.user_name()
        cleaned = "".join(ch for ch in raw if ch.isalnum())
        if len(cleaned) < 5: continue
        cleaned = cleaned[:20]  # Trim to max 20 chars
        if USERNAME_RE.match(cleaned) and cleaned not in Person.usernames:
            Person.usernames.add(cleaned)
            return cleaned
    raise RuntimeError(
        f"Could not generate a unique username after {attempts} attempts "
        f"({len(Person.usernames)} already taken)."
    )

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

def sample_log_balance(min_base: float, max_factor: float) -> int:
    """Sample a 'log-like' balance:
    balance = min_base * (max_factor ** u),  u ~ U(0,1)"""
    u = random.random()
    return int(min_base * (max_factor ** u))

def weighted_sample_no_replace(items, weights, k):
    """Weighted sampling without replacement via Efraimidis–Spirakis.
    Each item gets key = u**(1/w), u ~ U(0,1); the k items with the largest
    keys win. Probability of inclusion is proportional to weight."""
    keyed = sorted((random.random() ** (1.0 / w), x) for x, w in zip(items, weights))
    return [x for _, x in keyed[-k:]]

class Person:
    idx = 1  # Class variable to assign unique user IDs
    usernames = set()  # Class variable to track used usernames

    @classmethod
    def reset_state(cls):
        """Reset class-level state so the generator can run multiple times in
        the same Python process without spurious username collisions."""
        cls.idx = 1
        cls.usernames.clear()

    def __init__(self):
        # Each AIUser gets a fixed random seed so behaviour can be reproducible
        self.seed = random.randint(1_000_000, 10_000_000)
        self._identity()         # Full name, username, email, birthdate
        self._trade_properties() # Aggressiveness, decision interval, trade probability
        self._portfolio()        # Balance, cash reserves, min/max open positions, watchlist, holdings
        self._order_types()      # Probabilities for market/slippage orders, buy bias
        self._trade_limits()     # Slippage tolerance, limit offsets, per-position max, min/max trade amounts, daily limits

    def _identity(self):
        self.user_id   = Person.idx
        Person.idx    += 1                      # Increment for next person
        self.full_name = fake.name()            # Generate full name
        self.username  = generate_username()    # Generate valid username
        self.email     = f"{self.username}@{fake.free_email_domain()}"
        self.birthdate = fake.date_of_birth(minimum_age=18, maximum_age=80)

    def _trade_properties(self):
        # Slight bias towards lower aggressiveness (more conservative bots).
        base_agg = skewed01(skew=AGG_SKEW)
        self.aggressive = clamp01(jitter(base_agg, rel=AGG_JITTER))

        # More aggressive → shorter interval.
        base_interval = INTERVAL_BASE + INTERVAL_SLOPE * self.aggressive
        self.interval_seconds = int(max(INTERVAL_FLOOR, jitter(base_interval, rel=INTERVAL_JITTER)))

        # Probability to trade each decision
        base_trade_prob = TRADE_PROB_BASE + TRADE_PROB_SLOPE * self.aggressive
        self.trade_prob = clamp01(jitter(base_trade_prob, rel=TRADE_PROB_JITTER))

        # Strategy: fixed for now, could vary based on aggressiveness later
        self.strategy = random.choice(STRATEGY_CHOICES)

    def _portfolio(self):
        # Starting balance: log-distributed from $10,000 to ~$500,000
        self.balance = sample_log_balance(min_base=BALANCE_MIN, max_factor=BALANCE_MAX_FACTOR)

        # Cash reserves: aggressive bots keep less cash.
        base_max_cash = MAX_CASH_BASE + MAX_CASH_SLOPE * self.aggressive
        self.max_cash = clamp01(jitter(base_max_cash, rel=MAX_CASH_JITTER))
        self.min_cash = self.max_cash * random.uniform(MIN_CASH_FRACTION_LO, MIN_CASH_FRACTION_HI)

        # Min/Max open positions: more aggressive bots hold more stocks.
        if self.aggressive < STOCKS_AGG_LOW_THR:
            self.min_stocks = random.randint(*STOCKS_LOW_MIN_RANGE)
            self.max_stocks = random.randint(*STOCKS_LOW_MAX_RANGE)
        elif self.aggressive < STOCKS_AGG_MID_THR:
            self.min_stocks = random.randint(*STOCKS_MID_MIN_RANGE)
            self.max_stocks = random.randint(*STOCKS_MID_MAX_RANGE)
        else:
            self.min_stocks = random.randint(*STOCKS_HIGH_MIN_RANGE)
            self.max_stocks = random.randint(*STOCKS_HIGH_MAX_RANGE)

        # Watchlist: composition biased toward big-caps by 1/sid**α
        # (α = WATCHLIST_WEIGHT_ALPHA). Holdings split equally across picks.
        watchlist_extra = random.randint(WATCHLIST_EXTRA_LO, WATCHLIST_EXTRA_HI)
        watchlist_size = min(len(STOCKS), self.max_stocks + watchlist_extra)
        ids = list(STOCKS)
        weights = [1.0 / (sid ** WATCHLIST_WEIGHT_ALPHA) for sid in ids]
        watchlist = weighted_sample_no_replace(ids, weights, watchlist_size)
        self.watchlist_csv = ",".join(str(sid) for sid in sorted(watchlist))

        # Initial holdings: pick stocks from the watchlist, then split the
        # target stock value equally across them. Cap bias lives in watchlist
        # selection; sizing is uniform per picked stock.
        n_stocks = random.randint(self.min_stocks, self.max_stocks)
        portfolio = random.sample(watchlist, n_stocks)
        cash_frac = (self.min_cash * 2 + self.max_cash) / 3
        target_stock_value = self.balance * (1 - cash_frac)

        # Adjust quantity to be a whole number of shares.
        per_stock_value = target_stock_value / len(portfolio)
        self.holdings = { sid: int(per_stock_value // STOCKS[sid]["price"]) for sid in portfolio }

        # Adjust balance to account for the cost of initial holdings
        spent_on_stocks = sum(qty * STOCKS[sid]["price"] for sid, qty in self.holdings.items())
        self.balance -= spent_on_stocks

    def _order_types(self):
        # Probabilities for order types
        self.use_market = USE_MARKET_BASE + USE_MARKET_RANGE * skewed01(skew=USE_MARKET_SKEW)
        self.use_slip   = USE_SLIP_BASE   + USE_SLIP_RANGE   * skewed01(skew=USE_SLIP_SKEW)

        # Buy bias: slightly >50% buys for aggressive bots
        base_buy_bias = BUY_BIAS_BASE + BUY_BIAS_SLOPE * self.aggressive
        buy_bias      = clamp01(jitter(base_buy_bias, rel=BUY_BIAS_JITTER))
        self.buy_bias = max(BUY_BIAS_MIN, min(BUY_BIAS_MAX, buy_bias))

        # Extreme-reaction randomness: skewed toward 0, capped at 0.5.
        self.extreme_randomness = 0.5 * skewed01(skew=EXTREME_RANDOMNESS_SKEW)

    def _trade_limits(self):
        # Slippage tolerance: more aggressive bots accept higher slippage.
        base_slip_tol = SLIP_TOL_BASE + SLIP_TOL_SLOPE * self.aggressive
        self.slippage_tolerance = clamp01(jitter(base_slip_tol, rel=SLIP_TOL_JITTER))

        # Limit offsets: more aggressive bots use wider limits.
        base_max_limit = MAX_LIMIT_BASE + MAX_LIMIT_SLOPE * self.aggressive
        max_limit = clamp01(jitter(base_max_limit, rel=MAX_LIMIT_JITTER))
        min_limit = max(MIN_LIMIT_FLOOR, max_limit * random.uniform(MIN_LIMIT_FRACTION_LO, MIN_LIMIT_FRACTION_HI))
        self.min_limit_offset = clamp01(min_limit)
        self.max_limit_offset = clamp01(max_limit)

        # Per-position max: cap at 1/max_stocks so max_stocks positions can fit simultaneously.
        base_per_pos = PER_POS_BASE + PER_POS_SLOPE * self.aggressive
        portfolio_max = 1.0 / self.max_stocks
        per_pos_max = clamp01(jitter(base_per_pos, rel=PER_POS_JITTER))
        self.per_pos_max = min(per_pos_max, portfolio_max)

        # Min/Max trade amounts as fractions of per_pos_max
        self.min_trade_amount = self.per_pos_max * random.uniform(MIN_TRADE_FRACTION_LO, MIN_TRADE_FRACTION_HI)
        self.max_trade_amount = self.per_pos_max * random.uniform(MAX_TRADE_FRACTION_LO, MAX_TRADE_FRACTION_HI)

        # Daily limits: more aggressive bots trade more often.
        base_trade = int(MAX_DAILY_TRADES_BASE + MAX_DAILY_TRADES_SLOPE * self.aggressive)
        self.max_daily_trades = max(MAX_DAILY_TRADES_FLOOR, int(jitter(base_trade, rel=MAX_DAILY_TRADES_JITTER)))
        base_open_orders = int(MAX_OPEN_ORDERS_BASE + MAX_OPEN_ORDERS_SLOPE * self.aggressive)
        self.max_orders = max(MAX_OPEN_ORDERS_FLOOR, int(jitter(base_open_orders, rel=MAX_OPEN_ORDERS_JITTER)))

    def ToIdentityList(self):
        return [
            self.user_id,
            self.username,
            self.full_name,
            self.email,
            self.birthdate.isoformat() # birthdate written as ISO "YYYY-MM-DD" string
        ]

    def ToProfileList(self):
        return [
            self.user_id,                                   # int: user id
            self.seed,                                      # int: RNG seed
            self.interval_seconds,                          # int: decision interval (seconds)
            round(self.trade_prob, 4),                      # float: trade probability
            round(self.use_market, 4),                      # float: probability of market orders
            round(self.use_slip, 4),                        # float: probability of slippage orders
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
            self.watchlist_csv,                             # str: watchlist CSV of stock IDs
            self.strategy,                                  # int: strategy id
            round(self.extreme_randomness, 4)               # float: extreme-reaction randomness [0, 0.5]
        ]

    def ToHoldingList(self):
        result = [self.user_id, round(self.balance, 2)]
        for stock_id in STOCKS:
            result.append(self.holdings.get(stock_id, 0))
        return result

    def __repr__(self):
        return (f"Person({self.username!r}, Aggressive={self.aggressive:.2f}, balance=${self.balance:.2f})")
