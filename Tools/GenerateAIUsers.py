# run_generate_aiusers.py

import os
import random
import shutil
import statistics
from pathlib import Path

from Config import (
    STOCKS, CROSS_LISTED_STOCK_IDS, EUR_ONLY_STOCK_IDS,
    FX_BASE_RATES, LISTING_PRICE_JITTER,
    ARBITRAGE_COHORT_SIZE, HOUSE_USER_ID_OFFSET,
    HOUSE_SEED_BALANCE_USD, HOUSE_SEED_BALANCE_EUR,
    MARKET_MAKER_COHORT_SIZE,
    JUMP_AGGRESSOR_USER_ID_OFFSET,
    JUMP_AGGRESSOR_SEED_BALANCE_USD, JUMP_AGGRESSOR_SEED_BALANCE_EUR,
    JUMP_AGGRESSOR_SEED_SHARES,
)
from Person import Person, fake
from ExcelLayout import *

# Where to store the Excel. The workbook must exist in BOTH the client and the server Resources/Raw so
# the embedded-seed path (server) and any client-side use stay in sync.
BASE_DIR = Path(__file__).resolve().parent
EXCEL_PATH = BASE_DIR.parent / "KieshStockExchange" / "Resources" / "Raw" / "AIUserData.xlsx"
SERVER_EXCEL_PATH = BASE_DIR.parent / "KieshStockExchange.Server" / "Resources" / "Raw" / "AIUserData.xlsx"
NUM_PEOPLE = 20000

# §P6 "layout/userinfo skip for speed": set KSE_FAST_GEN=1 to skip the dark-theme styling + autofit
# (by far the slowest step over 20000 rows). Data is identical; only the cosmetic sheet styling differs.
FAST_GEN = bool(os.environ.get("KSE_FAST_GEN"))

# Seeding both `random` and the Faker instance
GENERATOR_SEED = 42


def generate_aiuser_excel(excel_path: Path = EXCEL_PATH, num_people: int = NUM_PEOPLE) -> None:
    """
    Create/refresh the AIUser Excel with Identity, Preference, Holding and AIUserTable.
    """

    if GENERATOR_SEED is not None:
        random.seed(GENERATOR_SEED)
        fake.seed_instance(GENERATOR_SEED)

    # Load or create workbook
    wb = load_or_create_workbook(str(excel_path))
    print(f"✅ Loaded or created workbook at {excel_path}")

    # Create/clear sheets and write header rows
    sheets: dict[str, Worksheet] = {}
    # Holding sheet uses ticker symbols as human-readable column headers.
    tickers = [data["ticker"] for data in STOCKS.values()]

    # index, expecting [Stocks, Listings, Identity, Profile, Holding].
    sheets["Stocks"] = prepare_stocks_sheet(wb)
    sheets["Listings"] = prepare_listings_sheet(wb)
    sheets["Identity"] = prepare_identity_sheet(wb)
    sheets["Profile"] = prepare_profile_sheet(wb)
    sheets["Holding"] = prepare_holding_sheet(wb, tickers)

    print("✅ Prepared all AIUser sheets.")


    # Append stock data
    for stock_id, data in STOCKS.items():
        sheets["Stocks"].append([stock_id, data["ticker"], data["name"]])

    # Append listings data. Cross-listed → USD primary + EUR row at usd/fx_rate
    # ± LISTING_PRICE_JITTER. USD-only → single USD row. EUR-only → single
    # EUR row at usd/fx_rate. Matches StockListingSeed in the C# fall-back.
    cross_set = set(CROSS_LISTED_STOCK_IDS)
    eur_only_set = set(EUR_ONLY_STOCK_IDS)
    eur_per_usd = 1.0 / FX_BASE_RATES["EUR/USD"]

    def _jittered(value: float) -> float:
        return value * (1.0 + random.uniform(-LISTING_PRICE_JITTER, LISTING_PRICE_JITTER))

    for stock_id, data in STOCKS.items():
        usd = data["price"]
        if stock_id in cross_set:
            sheets["Listings"].append([stock_id, "USD", True, round(usd, 2)])
            sheets["Listings"].append([stock_id, "EUR", False, round(_jittered(usd * eur_per_usd), 2)])
        elif stock_id in eur_only_set:
            sheets["Listings"].append([stock_id, "EUR", True, round(usd * eur_per_usd, 2)])
        else:
            sheets["Listings"].append([stock_id, "USD", True, round(usd, 2)])

    # Reset class-level state so user_ids start at 1
    Person.reset_state()

    # Two-pass generation: build everyone, then back-fill the cash-injection
    # knobs using the population median portfolio value as the reference.
    people = [Person() for _ in range(num_people)]
    median_pv = statistics.median(p.portfolio_value() for p in people)
    for p in people:
        p.assign_cash_injection_knobs(reference_portfolio_value=median_pv)
    for p in people:
        sheets["Identity"].append(p.ToIdentityList())
        sheets["Holding"].append(p.ToHoldingList())
        sheets["Profile"].append(p.ToProfileList())

    print(f"✅ Generated {num_people} AI users (median seeded portfolio value: ${median_pv:,.2f}).")

    # Human admin appended at the end — Identity + Holding only (no Profile = not a bot).
    # Password is the seeder's shared "hallo123"; IsAdmin promotes this one row.
    # Holding rows now carry a trailing BalanceSecondary column (0 = single-currency).
    admin_id = num_people + 1
    sheets["Identity"].append([admin_id, "admin", "Admin User", "admin@kse.local", "1990-01-01", True])
    sheets["Holding"].append([admin_id, 1_000_000.00] + [0 for _ in STOCKS] + [0])
    print(f"✅ Appended admin account (UserId {admin_id}, username 'admin').")

    # §3.7 Platform house account — Identity + dual-currency Holding, NO Profile (so it is never a
    # bot / never in the fleet). The server reads its UserId from Platform:HouseUserId (default
    # NUM_PEOPLE + 2). Seeded large in BOTH currencies so it always has inventory to settle the FX
    # conversion spread it accrues. USD is the home (Balance); EUR is the secondary column.
    house_id = num_people + HOUSE_USER_ID_OFFSET
    sheets["Identity"].append([house_id, "house", "Platform House", "house@kse.local", "1990-01-01", False])
    sheets["Holding"].append([house_id, HOUSE_SEED_BALANCE_USD] + [0 for _ in STOCKS] + [HOUSE_SEED_BALANCE_EUR])
    print(f"✅ Appended platform house account (UserId {house_id}, username 'house').")

    # §3.7 Arbitrage cohort — Identity + Holding + Profile (strategy=5), generated separately from
    # the random fleet. Dual-currency seed, cash-injection disabled, watchlist = cross-listed stocks.
    cohort_start = house_id + 1
    for i in range(ARBITRAGE_COHORT_SIZE):
        bot = Person.make_arbitrage(cohort_start + i)
        sheets["Identity"].append(bot.ToIdentityList())
        sheets["Holding"].append(bot.ToHoldingList())
        sheets["Profile"].append(bot.ToProfileList())
    print(f"✅ Appended {ARBITRAGE_COHORT_SIZE} arbitrage bots (UserIds {cohort_start}–{cohort_start + ARBITRAGE_COHORT_SIZE - 1}).")

    # §mm-cohort: market-maker cohort (strategy=6), generated separately from the random fleet. Single
    # home-currency seed, cash-injection disabled, full-board watchlist. DEFAULT SIZE 0 ⇒ nothing appended
    # ⇒ byte-identical seed; set MARKET_MAKER_COHORT_SIZE > 0 to seed the cohort for an MM bake.
    mm_start = cohort_start + ARBITRAGE_COHORT_SIZE
    for i in range(MARKET_MAKER_COHORT_SIZE):
        bot = Person.make_market_maker(mm_start + i)
        sheets["Identity"].append(bot.ToIdentityList())
        sheets["Holding"].append(bot.ToHoldingList())
        sheets["Profile"].append(bot.ToProfileList())
    if MARKET_MAKER_COHORT_SIZE > 0:
        print(f"✅ Appended {MARKET_MAKER_COHORT_SIZE} market-maker bots (UserIds {mm_start}–{mm_start + MARKET_MAKER_COHORT_SIZE - 1}).")

    # §fat-tail jumps: dedicated aggressor account — Identity + Holding (cash + per-stock share float),
    # NO Profile (never a bot / never in the fleet). Reserved at NUM_PEOPLE + JUMP_AGGRESSOR_USER_ID_OFFSET
    # (default 20100), appended LAST so it shifts NO existing UserId ⇒ byte-identical-off. JumpService reads
    # its UserId from Bots:Jumps:AggressorUserId. Inert until Bots:Jumps:Enabled.
    jump_id = num_people + JUMP_AGGRESSOR_USER_ID_OFFSET
    sheets["Identity"].append([jump_id, "jumpdesk", "Jump Aggressor", "jumpdesk@kse.local", "1990-01-01", False])
    sheets["Holding"].append([jump_id, JUMP_AGGRESSOR_SEED_BALANCE_USD]
                             + [JUMP_AGGRESSOR_SEED_SHARES for _ in STOCKS]
                             + [JUMP_AGGRESSOR_SEED_BALANCE_EUR])
    print(f"✅ Appended jump aggressor account (UserId {jump_id}, username 'jumpdesk').")


    # Apply dark theme and autofit columns (skipped in fast mode — purely cosmetic, the slowest step).
    if FAST_GEN:
        print("⚡ KSE_FAST_GEN set — skipping dark theme + autofit (layout/userinfo skip).")
    else:
        for ws in sheets.values():
            apply_dark_theme(ws)
            autofit_columns(ws)

    # Drop the placeholder sheet that Workbook() creates by default.
    if "Template" in wb.sheetnames:
        del wb["Template"]

    # Save file (client copy) then mirror to the server copy so both Resources/Raw stay identical.
    wb.save(str(excel_path))
    SERVER_EXCEL_PATH.parent.mkdir(parents=True, exist_ok=True)
    shutil.copyfile(str(excel_path), str(SERVER_EXCEL_PATH))
    print(f"✅ Saved all {num_people} AI users to:\n   {excel_path}\n   {SERVER_EXCEL_PATH}")


if __name__ == "__main__":
    generate_aiuser_excel()
