# run_generate_aiusers.py

import random
import statistics
from pathlib import Path

from Config import (
    STOCKS, CROSS_LISTED_STOCK_IDS, EUR_ONLY_STOCK_IDS,
    FX_BASE_RATES, LISTING_PRICE_JITTER,
)
from Person import Person, fake
from ExcelLayout import *

# Where to store the Excel
BASE_DIR = Path(__file__).resolve().parent
EXCEL_PATH = BASE_DIR.parent / "KieshStockExchange" / "Resources" / "Raw" / "AIUserData.xlsx"
NUM_PEOPLE = 20000

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
    admin_id = num_people + 1
    sheets["Identity"].append([admin_id, "admin", "Admin User", "admin@kse.local", "1990-01-01", True])
    sheets["Holding"].append([admin_id, 1_000_000.00] + [0 for _ in STOCKS])
    print(f"✅ Appended admin account (UserId {admin_id}, username 'admin').")


    # Apply dark theme and autofit columns
    for ws in sheets.values():
        apply_dark_theme(ws)
        autofit_columns(ws)

    # Drop the placeholder sheet that Workbook() creates by default.
    if "Template" in wb.sheetnames:
        del wb["Template"]

    # Save file
    wb.save(str(excel_path))
    print(f"✅ Applied dark theme and saved all {num_people} AI users.")


if __name__ == "__main__":
    generate_aiuser_excel()
