# run_generate_aiusers.py

import random
from pathlib import Path

from Config import STOCKS
from Person import Person, fake
from ExcelLayout import *

# Where to store the Excel
BASE_DIR = Path(__file__).resolve().parent
EXCEL_PATH = BASE_DIR.parent / "KieshStockExchange" / "Resources" / "Raw" / "AIUserData.xlsx"
NUM_PEOPLE = 20000

# Seeding both `random` and the Faker instance makes the generated Excel
# byte-identical across runs. Set to None to get a fresh population each run.
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
    print(f"[OK] Loaded or created workbook at {excel_path}")

    # Create/clear sheets and write header rows
    sheets: dict[str, Worksheet] = {}
    # Holding sheet uses ticker symbols as human-readable column headers.
    tickers = [data["ticker"] for data in STOCKS.values()]

    # Sheet creation order matters: C# ExcelImportService reads by sheet
    # index, expecting [Stocks, Identity, Profile, Holding].
    sheets["Stocks"] = prepare_stocks_sheet(wb)
    sheets["Identity"] = prepare_identity_sheet(wb)
    sheets["Profile"] = prepare_profile_sheet(wb)
    sheets["Holding"] = prepare_holding_sheet(wb, tickers)

    print("[OK] Prepared all AIUser sheets.")


    # Append stock data
    for stock_id, data in STOCKS.items():
        sheets["Stocks"].append([stock_id, data["ticker"], data["name"], data["price"]])

    # Reset class-level state so user_ids start at 1 and the username pool is
    # empty for this run (otherwise a second call in the same process would
    # spin in generate_username trying to find a fresh name).
    Person.reset_state()

    # Generate people and append rows
    for _ in range(num_people):
        p = Person()
        sheets["Identity"].append(p.ToIdentityList())
        sheets["Holding"].append(p.ToHoldingList())
        sheets["Profile"].append(p.ToProfileList())

    print(f"[OK] Generated {num_people} AI users.")


    # Apply dark theme and autofit columns
    for ws in sheets.values():
        apply_dark_theme(ws)
        autofit_columns(ws)

    # Drop the placeholder sheet that Workbook() creates by default.
    if "Template" in wb.sheetnames:
        del wb["Template"]

    # Save file
    wb.save(str(excel_path))
    print(f"[OK] Applied dark theme and saved all {num_people} AI users.")


if __name__ == "__main__":
    generate_aiuser_excel()
