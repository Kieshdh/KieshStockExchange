# run_generate_aiusers.py

from pathlib import Path

from Person import (Person, TICKERS, COMPANY_NAMES, PRICES, STOCKIDS)
from ExcelLayout import *

# Where to store the Excel
BASE_DIR = Path(__file__).resolve().parent
EXCEL_PATH = BASE_DIR.parent / "KieshStockExchange" / "Resources" / "Raw" / "AIUserData.xlsx"
NUM_PEOPLE = 100 


def generate_aiuser_excel(excel_path: Path = EXCEL_PATH, num_people: int = NUM_PEOPLE) -> None:
    """
    Create/refresh the AIUser Excel with Identity, Preference, Holding and AIUserTable.
    """

    # Load or create workbook
    wb = load_or_create_workbook(str(excel_path))
    print(f"✅ Loaded or created workbook at {excel_path}")

    # Create/clear sheets and write header rows
    sheets = dict() # type: Dict[str, Worksheet]
    #sheets["Stocks"] = prepare_stocks_sheet(wb)
    sheets["Identity"] = prepare_identity_sheet(wb)
    sheets["Holding"] = prepare_holding_sheet(wb, TICKERS)
    sheets["Profile"] = prepare_profile_sheet(wb)

    print("✅ Prepared all AIUser sheets.")


    # Append stock data
    #for ticker in TICKERS:
    #    sheets["Stocks"].append([STOCKIDS[ticker], ticker, COMPANY_NAMES[ticker], PRICES[ticker]])

    # Reset the Person index counter (so user_ids start at 1 again)
    Person.idx = 1

    # Generate people and append rows
    for _ in range(num_people):
        p = Person()
        sheets["Identity"].append(p.ToIdentityList())
        sheets["Holding"].append(p.ToHoldingList())
        sheets["Profile"].append(p.ToProfileList())

    print(f"✅ Generated {num_people} AI users.")


    # Apply dark theme and autofit columns
    for ws in sheets.values():
        apply_dark_theme(ws)
        autofit_columns(ws)

    # Save file
    wb.save(str(excel_path))
    print(f"✅ Applied dark theme and saved all {num_people} AI users.")


if __name__ == "__main__":
    generate_aiuser_excel()
