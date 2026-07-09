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

def clamp(x: float, lo: float, hi: float) -> float:
    """Clamp a float into [lo, hi]."""
    return lo if x < lo else hi if x > hi else x

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

def weighted_choice(weights_dict):
    """Pick a single key from a {key: weight} dict where weights sum to 1.
    Used by Person._identity to draw a home currency from HOME_CURRENCY_WEIGHTS."""
    r = random.random()
    cum = 0.0
    last_key = None
    for key, w in weights_dict.items():
        cum += w
        last_key = key
        if r <= cum:
            return key
    return last_key  # floating-point residue — return the last key

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
        self._advanced_orders()  # Per-strategy advanced-order probabilities (stop/trailing/short/brackets)
        self._tiers()            # §P6 tiered-limit bands (Mid/Far), protective-stop distance band, Far budget
        # Cash-injection knobs are assigned by a second pass in GenerateAIUsers
        # once the population median is known. Default to 0 so an un-assigned
        # Person still passes C# validation (zero frequency = bot never injects).
        self.cash_injection_frequency_prc = 0.0
        self.cash_injection_amount_prc = 0.0
        # §3.7 arbitrage-cohort params + dual-currency secondary balance. Default 0 = inert: a normal
        # bot ignores them and seeds a single (home-currency) fund. make_arbitrage() overrides these.
        self.min_arbitrage_rate_prc = 0.0
        self.max_inventory_per_stock = 0
        self.conversion_cadence_seconds = 0
        self.balance_secondary = 0.0

    def _identity(self):
        self.user_id   = Person.idx
        Person.idx    += 1                      # Increment for next person
        self.full_name = fake.name()            # Generate full name
        self.username  = generate_username()    # Generate valid username
        self.email     = f"{self.username}@{fake.free_email_domain()}"
        self.birthdate = fake.date_of_birth(minimum_age=18, maximum_age=80)
        self.home_currency = weighted_choice(HOME_CURRENCY_WEIGHTS)

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

        # Strategy: drawn from the NON-EVEN sentiment-dynamics ratios (STRATEGY_WEIGHTS) so the population
        # is follow-leaning during a move and reversion-heavy at extremes (loop gain G≈1).
        self.strategy = random.choices(
            list(STRATEGY_WEIGHTS.keys()), weights=list(STRATEGY_WEIGHTS.values()), k=1)[0]

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

        # Watchlist: composition biased toward big-caps by 1/sid**α (α = WATCHLIST_WEIGHT_ALPHA).
        # §reseed-2026-07 (council): CURRENCY-GATE the draw to the bot's home-currency listings — it only
        # watches (and, via the sample below, holds) stocks it can actually trade. Removes wasted watchlist
        # slots + the stranded cross-currency holdings bug, and concentrates the minority currency's fleet
        # on its own books. USD-home: everything except EUR-only. EUR-home: cross-listed + EUR-only.
        watchlist_extra = random.randint(WATCHLIST_EXTRA_LO, WATCHLIST_EXTRA_HI)
        if self.home_currency == "EUR":
            eligible = set(CROSS_LISTED_STOCK_IDS) | set(EUR_ONLY_STOCK_IDS)
        else:
            eligible = set(STOCKS) - set(EUR_ONLY_STOCK_IDS)
        ids = [sid for sid in STOCKS if sid in eligible]
        watchlist_size = min(len(ids), self.max_stocks + watchlist_extra)
        weights = [1.0 / (sid ** WATCHLIST_WEIGHT_ALPHA) for sid in ids]
        watchlist = weighted_sample_no_replace(ids, weights, watchlist_size)
        self.watchlist_csv = ",".join(str(sid) for sid in sorted(watchlist))

        # Initial holdings: pick stocks from the watchlist, then split the
        # target stock value equally across them. Cap bias lives in watchlist
        # selection; sizing is uniform per picked stock.
        n_stocks = random.randint(self.min_stocks, self.max_stocks)
        portfolio = random.sample(watchlist, n_stocks)
        # Seed cash% sits at the band MIDPOINT so it coincides with the runtime cash-homeostasis
        # rest-point (the midpoint of [MinCashReservePrc, MaxCashReservePrc]). Centering the seed in the
        # band makes the controller hit the Min/Max walls symmetrically → no systematic sell-lean / drift.
        cash_frac = (self.min_cash + self.max_cash) / 2
        target_stock_value = self.balance * (1 - cash_frac)

        # EUR-home bots see every USD reference price scaled by 1 / (EUR per
        # USD) so the sizing math (balance ÷ price) lands sensible quantities
        # in EUR units. USD-home bots use the USD prices unchanged.
        eur_per_usd = 1.0 / FX_BASE_RATES["EUR/USD"]
        price_scale = eur_per_usd if self.home_currency == "EUR" else 1.0

        # Adjust quantity to be a whole number of shares.
        per_stock_value = target_stock_value / len(portfolio)
        self.holdings = {
            sid: int(per_stock_value // (STOCKS[sid]["price"] * price_scale))
            for sid in portfolio
        }

        # Adjust balance to account for the cost of initial holdings
        spent_on_stocks = sum(qty * STOCKS[sid]["price"] * price_scale
                              for sid, qty in self.holdings.items())
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

        # Sentiment-dynamics §: per-bot lateness L ∈ [0,1]. Right-skewed → mostly EARLY momentum bots with a
        # small high-L (FOMO) tail. Only consumed by the momentum cohort in C#; inert for other strategies.
        self.lateness = clamp01(skewed01(skew=LATENESS_SKEW))

        # Round 2 §0012 (extension E5): per-bot round-trip vs flip preference. Per-strategy point
        # value (from Config.ROUNDTRIP_BIAS_PER_STRATEGY) jittered by ROUNDTRIP_BIAS_JITTER. Inert
        # when Bots:Advanced:BracketFlip is off (server reads it only inside the _bracketFlip path).
        center = ROUNDTRIP_BIAS_PER_STRATEGY.get(self.strategy, 0.5)
        self.roundtrip_bias = clamp01(jitter(center, rel=ROUNDTRIP_BIAS_JITTER))

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

    def _advanced_orders(self):
        # Per-bot advanced-order probabilities, drawn uniformly from this bot's strategy profile so
        # behaviour matches the strategy (trend followers trail, mean-reverters bracket/short, etc.).
        # Falls back to the Random (3) profile for any strategy id not in the table.
        prof = ADVANCED_PROFILES.get(self.strategy, ADVANCED_PROFILES[3])
        self.stop_prob          = clamp01(random.uniform(*prof["stop"]))
        self.trailing_prob      = clamp01(random.uniform(*prof["trailing"]))
        self.short_prob         = clamp01(random.uniform(*prof["short"]))
        # R4 §0009 Stage 6C (2026-06-13): bracket probabilities permanently zeroed across all bots.
        # The Stage 6C A/B soak showed brackets caused the residual bear-tail asymmetry
        # (gap 37.7pp → 9.6pp when removed) AND throttled throughput (-43%). The bracket cohort
        # injects SL-cascade-driven price spikes that aren't realistic and aren't structural to
        # the bot ecosystem. Stop / trailing / short probabilities remain bot-strategy seeded.
        # Consume the same two RNG draws to preserve byte-identical determinism for every
        # downstream draw (per-bot stocks, holdings, etc.).
        _ = random.uniform(*prof["long_bracket"])
        _ = random.uniform(*prof["short_bracket"])
        self.long_bracket_prob  = 0.0
        self.short_bracket_prob = 0.0

    def _tiers(self):
        # §P6 tiered limit ladder + protective-stop band + Far budget. Close is the existing
        # min/max_limit_offset (set in _trade_limits). Mid and Far are standing walls further out;
        # ordering (Close ≤ Mid ≤ Far) and StopDistanceMax < FarLimitMin are enforced here so the C#
        # AIUser.ValidateSizing invariants always hold and a fired stop stays inside the Far walls.
        self.mid_limit_min = clamp01(random.uniform(*MID_LIMIT_MIN_RANGE))
        self.mid_limit_max = clamp01(max(self.mid_limit_min, random.uniform(*MID_LIMIT_MAX_RANGE)))
        self.far_limit_min = clamp01(max(self.mid_limit_max, random.uniform(*FAR_LIMIT_MIN_RANGE)))
        self.far_limit_max = clamp01(max(self.far_limit_min, random.uniform(*FAR_LIMIT_MAX_RANGE)))

        # Protective stop strictly inside the Far walls.
        stop_cap = self.far_limit_min * 0.9
        self.stop_distance_max = clamp01(min(random.uniform(*STOP_DISTANCE_MAX_RANGE), stop_cap))
        self.stop_distance_min = clamp01(self.stop_distance_max * random.uniform(*STOP_DISTANCE_MIN_FRACTION))

        self.far_budget = clamp01(random.uniform(*FAR_BUDGET_RANGE))

        # §P6 per-bot take-profit band (bracket TP legs draw from [tp_offset_min, tp_offset_max]).
        self.tp_offset_min = clamp01(random.uniform(*TP_OFFSET_MIN_RANGE))
        self.tp_offset_max = clamp01(max(self.tp_offset_min, random.uniform(*TP_OFFSET_MAX_RANGE)))

    def portfolio_value(self) -> float:
        # Total seeded wealth = remaining cash + market value of initial holdings.
        return self.balance + sum(qty * STOCKS[sid]["price"]
                                  for sid, qty in self.holdings.items())

    def assign_cash_injection_knobs(self, reference_portfolio_value: float) -> None:
        # Smaller bots get higher frequency and higher amount via inverse-size
        # scaling. Floors/caps protect both ends so extremely small or large
        # bots stay within the C# validator bounds.
        size_ratio = self.portfolio_value() / reference_portfolio_value
        size_factor = (1.0 / max(size_ratio, 0.1)) ** CASH_INJECTION_SIZE_ALPHA
        self.cash_injection_frequency_prc = clamp(
            jitter(CASH_INJECTION_BASE_FREQUENCY * size_factor, rel=CASH_INJECTION_JITTER),
            CASH_INJECTION_FREQ_FLOOR, CASH_INJECTION_FREQ_CAP)
        self.cash_injection_amount_prc = clamp(
            jitter(CASH_INJECTION_BASE_AMOUNT * size_factor, rel=CASH_INJECTION_JITTER),
            CASH_INJECTION_AMOUNT_FLOOR, CASH_INJECTION_AMOUNT_CAP)

    def ToIdentityList(self):
        return [
            self.user_id,
            self.username,
            self.full_name,
            self.email,
            self.birthdate.isoformat(), # birthdate written as ISO "YYYY-MM-DD" string
            False,                      # IsAdmin — generated bots are never admins
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
            self.max_orders,                                # int: max open orders
            self.watchlist_csv,                             # str: watchlist CSV of stock IDs
            self.strategy,                                  # int: strategy id
            round(self.extreme_randomness, 4),              # float: extreme-reaction randomness [0, 0.5]
            round(self.cash_injection_frequency_prc, 4),    # float: cash-injection frequency / cycle [0, 0.5]
            round(self.cash_injection_amount_prc, 6),       # float: cash-injection amount % of portfolio [0, 0.05]
            self.home_currency,                             # str: home currency ISO code (USD/EUR)
            round(self.stop_prob, 4),                       # float: P(stop-market sell) per tick
            round(self.trailing_prob, 4),                   # float: P(trailing-stop sell) per tick
            round(self.short_prob, 4),                      # float: P(open flat short) per tick
            round(self.long_bracket_prob, 4),               # float: P(long bracket) per tick
            round(self.short_bracket_prob, 4),              # float: P(short bracket) per tick
            # §P6 tiered-limit bands + protective-stop band + Far budget (order must match
            # ExcelLayout.prepare_profile_sheet; the server reads these by column name).
            round(self.mid_limit_min, 4),                   # float: MidLimitMinPrc
            round(self.mid_limit_max, 4),                   # float: MidLimitMaxPrc
            round(self.far_limit_min, 4),                   # float: FarLimitMinPrc
            round(self.far_limit_max, 4),                   # float: FarLimitMaxPrc
            round(self.stop_distance_min, 4),               # float: StopDistanceMinPrc
            round(self.stop_distance_max, 4),               # float: StopDistanceMaxPrc
            round(self.far_budget, 4),                      # float: FarBudgetPrc
            round(self.tp_offset_min, 4),                   # float: TpOffsetMinPrc
            round(self.tp_offset_max, 4),                   # float: TpOffsetMaxPrc
            # Sentiment-dynamics §: per-bot lateness L (must match ExcelLayout.prepare_profile_sheet order).
            round(self.lateness, 4),                        # float: Lateness
            # Round 2 §0012 (extension E5): round-trip vs flip preference.
            round(self.roundtrip_bias, 4),                  # float: RoundtripBiasPrc
            # §3.7 arbitrage cohort params (must match ExcelLayout.prepare_profile_sheet column order).
            round(self.min_arbitrage_rate_prc, 6),          # float: MinArbitrageRatePrc
            int(self.max_inventory_per_stock),              # int:   MaxInventoryPerStock
            int(self.conversion_cadence_seconds),           # int:   ConversionCadenceSeconds
        ]

    def ToHoldingList(self):
        result = [self.user_id, round(self.balance, 2)]
        for stock_id in STOCKS:
            result.append(self.holdings.get(stock_id, 0))
        # §3.7 trailing dual-currency column (0 for normal single-currency bots).
        result.append(round(self.balance_secondary, 2))
        return result

    @classmethod
    def make_arbitrage(cls, user_id: int) -> "Person":
        """§3.7 Build one arbitrage-cohort bot. Reuses a normal Person for all the (still-valid but
        unused) Profile fields, then overrides identity/strategy/seed-balances + the arbitrage knobs.
        Generated separately from the random fleet so STRATEGY_CHOICES never yields strategy 5."""
        p = cls()                               # fills every Profile field with valid values
        p.user_id = user_id                     # explicit id (the auto-assigned one is discarded)
        p.strategy = 5                          # AiStrategy.Arbitrage
        p.home_currency = "USD"
        p.interval_seconds = int(max(1, random.randint(*ARB_DECISION_INTERVAL)))

        # Self-funding: no cash injection, dual-currency cash, flat (no initial share holdings).
        p.cash_injection_frequency_prc = 0.0
        p.cash_injection_amount_prc = 0.0
        p.balance = ARB_SEED_BALANCE_USD
        p.balance_secondary = ARB_SEED_BALANCE_EUR
        p.holdings = {}

        # Watchlist = the cross-listed universe (the only stocks an arbitrage bot can couple).
        p.watchlist_csv = ",".join(str(sid) for sid in sorted(CROSS_LISTED_STOCK_IDS))

        # The three per-bot arbitrage knobs, jittered across the cohort.
        p.min_arbitrage_rate_prc = clamp01(random.uniform(*ARB_MIN_RATE_RANGE))
        p.max_inventory_per_stock = random.randint(*ARB_MAX_INVENTORY_RANGE)
        p.conversion_cadence_seconds = random.randint(*ARB_CONVERSION_CADENCE)
        return p

    @classmethod
    def make_market_maker(cls, user_id: int) -> "Person":
        """§mm-cohort: build one all-weather market-maker bot (AiStrategy.MarketMakerHouse=6). Generated
        separately from the random fleet so STRATEGY_CHOICES never yields strategy 6. Self-funded single
        home-currency (USD), flat holdings, full-board watchlist; MaxInventoryPerStock is the hard
        two-sided position cap the server-side quote math clamps against."""
        p = cls()                               # fills every Profile field with valid values
        p.user_id = user_id                     # explicit id (the auto-assigned one is discarded)
        p.strategy = 6                          # AiStrategy.MarketMakerHouse
        p.home_currency = "USD"
        p.interval_seconds = int(max(1, random.randint(*MM_DECISION_INTERVAL)))

        # Self-funding: no cash injection, single-currency cash, flat (no initial share holdings).
        p.cash_injection_frequency_prc = 0.0
        p.cash_injection_amount_prc = 0.0
        p.balance = MM_SEED_BALANCE_USD
        p.balance_secondary = 0.0
        p.holdings = {}

        # Cover the whole board in the home currency — an all-weather maker quotes every listed stock.
        p.watchlist_csv = ",".join(str(sid) for sid in sorted(STOCKS))

        # MaxInventoryPerStock is the live cap; the arbitrage-only knobs stay 0 (unused for strategy 6).
        p.min_arbitrage_rate_prc = 0.0
        p.max_inventory_per_stock = random.randint(*MM_MAX_INVENTORY_RANGE)
        p.conversion_cadence_seconds = 0
        return p

    @classmethod
    def make_rotator_bot(cls, user_id: int) -> "Person":
        """§rotator: build one estimate-driven rotational bot (AiStrategy.Rotator=7). Generated separately
        from the random fleet so STRATEGY_CHOICES never yields strategy 7. Dual-currency seed + EQUAL-VALUE
        holdings across ALL stocks + cash as one more equal bucket (so it always has inventory to SELL to fund
        a rotation in either book), full-board watchlist, self-funded. The arbitrage/MM knobs stay 0 (unused for strategy 7);
        the runtime flow valve is Bots:Rotator:ParticipationFraction, not a per-bot field."""
        p = cls()                               # fills every Profile field with valid values
        p.user_id = user_id                     # explicit id (the auto-assigned one is discarded)
        p.strategy = 7                          # AiStrategy.Rotator
        p.home_currency = "USD"
        p.interval_seconds = int(max(1, random.randint(*ROTATOR_DECISION_INTERVAL)))

        # Self-funding: no cash injection, dual-currency cash (buys are funded in the book the sells returned).
        p.cash_injection_frequency_prc = 0.0
        p.cash_injection_amount_prc = 0.0
        p.balance = ROTATOR_SEED_BALANCE_USD
        p.balance_secondary = ROTATOR_SEED_BALANCE_EUR

        # Equal-VALUE start: every listed stock seeded to the same market value (shares = value / seed price),
        # so cheaper stocks get more shares and every starting position is worth the same ⇒ always holds
        # something worth selling to fund a rotation.
        p.holdings = {sid: max(1, round(ROTATOR_VALUE_PER_STOCK / STOCKS[sid]["price"]))
                      for sid in STOCKS}

        # Cover the whole board — a rotator ranks every listed stock by the price-vs-estimate gap.
        p.watchlist_csv = ",".join(str(sid) for sid in sorted(STOCKS))

        # The arbitrage/MM-only knobs stay 0 (unused for strategy 7).
        p.min_arbitrage_rate_prc = 0.0
        p.max_inventory_per_stock = 0
        p.conversion_cadence_seconds = 0
        return p

    @classmethod
    def make_conviction_bot(cls, user_id: int) -> "Person":
        """§conviction: build one discretionary sentiment/sector-momentum bot (AiStrategy.Conviction=8). Generated
        separately from the random fleet so STRATEGY_CHOICES never yields strategy 8. CASH-HEAVY single-currency USD
        seed + a LIGHT diversified holding across the board (so the memoryless exit has inventory to act on),
        full-board watchlist, self-funded. NO per-bot trait columns: every personality dial (cash floor, risk
        appetite, conviction bar, sentiment sensitivity, chaser/fader lean, check-in cadence) is HASHED from the
        aiUserId at runtime by ConvictionDecisionService. The arbitrage/MM knobs stay 0 (unused for strategy 8)."""
        p = cls()                               # fills every Profile field with valid values
        p.user_id = user_id                     # explicit id (the auto-assigned one is discarded)
        p.strategy = 8                          # AiStrategy.Conviction
        p.home_currency = "USD"
        p.interval_seconds = int(max(1, random.randint(*CONVICTION_DECISION_INTERVAL)))

        # Self-funding: no cash injection, single-currency USD cash, MOSTLY CASH.
        p.cash_injection_frequency_prc = 0.0
        p.cash_injection_amount_prc = 0.0
        p.balance = CONVICTION_SEED_BALANCE_USD
        p.balance_secondary = 0.0

        # Light diversified start: a small equal-VALUE slice of every listed stock (shares = value / seed price),
        # far below the cash pile ⇒ cash-heavy, but every bot holds something the exit path can sell.
        p.holdings = {sid: max(1, round(CONVICTION_HOLDING_VALUE_PER_STOCK / STOCKS[sid]["price"]))
                      for sid in STOCKS}

        # Cover the whole board — a conviction bot ranks every listed stock by its Hot signal.
        p.watchlist_csv = ",".join(str(sid) for sid in sorted(STOCKS))

        # The arbitrage/MM-only knobs stay 0 (unused for strategy 8).
        p.min_arbitrage_rate_prc = 0.0
        p.max_inventory_per_stock = 0
        p.conversion_cadence_seconds = 0
        return p

    def __repr__(self):
        return (f"Person({self.username!r}, Aggressive={self.aggressive:.2f}, balance=${self.balance:.2f})")
