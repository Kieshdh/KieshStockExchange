# excel_layout.py

import os
from typing import Dict, Tuple

from openpyxl import Workbook, load_workbook
from openpyxl.worksheet.worksheet import Worksheet
from openpyxl.styles import (
    Font, PatternFill, GradientFill, Border, Side,
    Alignment, Protection, NamedStyle, Color
)
from openpyxl.utils import get_column_letter

# ────────────────────────────── SHEET STYLING ────────────────────────────────
HEADER_COLOR = Color(rgb="4A7A41")            # DarkGreen
HEADER_FONT_COLOR = Color(rgb="FFFFFF")       # White
ROW_FILL_COLOR = Color(rgb="0F1E25")          # DarkNavy
ROW_ALT_FILL_COLOR = Color(rgb="1E2E35")      # Lighter DarkNavy
TEXT_FONT_COLOR = Color(rgb="FFFFFF")         # White
BORDER_COLOR = Color(rgb="000000")            # Black
BACKGROUND_COLOR = Color(rgb="000000")        # Black

# ────────────────────────────── SHEET CREATION ────────────────────────────────

def load_or_create_workbook(excel_path: str) -> Workbook:
    """Load an existing Excel workbook if it exists, otherwise create a new one."""
    if os.path.exists(excel_path):
        wb = load_workbook(excel_path)
    else:
        wb = Workbook()
        wb.active.title = "Template"  # Minimal template if none exists
    return wb


def reset_or_create_sheet(wb: Workbook, name: str) -> Worksheet:
    """Return a worksheet with the given name that is empty.
    If it already exists, its contents are cleared."""
    if name in wb.sheetnames:
        ws = wb[name]
        ws.delete_rows(1, ws.max_row)
    else:
        ws = wb.create_sheet(name)
    return ws


def prepare_stocks_sheet(wb: Workbook) -> Worksheet:
    """Create/reset the Stocks sheet and write its header row."""
    ws = reset_or_create_sheet(wb, "Stocks")
    ws.append(["StockId", "Ticker", "CompanyName", "Price (USD)"])
    return ws


def prepare_identity_sheet(wb: Workbook) -> Worksheet:
    """Create/reset the Identity sheet and write its header row."""
    ws = reset_or_create_sheet(wb, "Identity")
    ws.append(["UserId", "Username", "FullName", "Email", "Birthdate"])
    return ws


def prepare_holding_sheet(wb: Workbook, tickers) -> Worksheet:
    """Create/reset the Holding sheet and write its header row.
    Includes dynamic ticker columns.
    """
    ws = reset_or_create_sheet(wb, "Holding")
    ws.append(["UserId", "Balance"] + list(tickers))
    return ws


def prepare_profile_sheet(wb: Workbook) -> Worksheet:
    """Create/reset the Profile sheet and write its header row."""
    ws = reset_or_create_sheet(wb, "Profile")
    ws.append([
        "UserId", "Seed", "DecisionIntervalSeconds", "TradeProb",
        "UseMarketProb", "UseSlippageMarketProb", "OnlineProb",
        "BuyBiasPrc", "MinTradeAmountPrc", "MaxTradeAmountPrc",
        "PerPositionMaxPrc", "MinCashReservePrc", "MaxCashReservePrc",
        "SlippageTolerancePrc", "MinLimitOffsetPrc", "MaxLimitOffsetPrc",
        "AggressivenessPrc", "MinOpenPositions", "MaxOpenPositions",
        "MaxDailyTrades", "MaxOpenOrders", "WatchlistCsv", "Strategy",
    ])
    return ws


def prepare_aiuser_sheets(wb: Workbook, tickers) -> Dict[str, Worksheet]:
    """Create/reset all needed AIUser sheets via dedicated helpers and return them."""
    ws_stocks = prepare_stocks_sheet(wb)
    ws_identity = prepare_identity_sheet(wb)
    ws_holding = prepare_holding_sheet(wb, tickers)
    ws_profile = prepare_profile_sheet(wb)

    return {
        "Stocks": ws_stocks,
        "Identity": ws_identity,
        "Holding": ws_holding,
        "Profile": ws_profile,
    }


# ───────────────────────────── THEME / STYLING ────────────────────────────────
def apply_dark_theme(ws: Worksheet) -> None:
    """Apply a predefined dark theme to the given worksheet. """
    # Determine worksheet size
    max_used_row = ws.max_row
    max_used_col = ws.max_column
    bg_max_row = max_used_row + 20
    bg_max_col = max_used_col + 20

    # Cell styles
    header_fill = PatternFill(start_color=HEADER_COLOR, end_color=HEADER_COLOR, fill_type="solid")
    row_fill_primary = PatternFill(start_color=ROW_FILL_COLOR, end_color=ROW_FILL_COLOR, fill_type="solid")
    row_fill_alt = PatternFill(start_color=ROW_ALT_FILL_COLOR, end_color=ROW_ALT_FILL_COLOR, fill_type="solid")
    black_fill = PatternFill(start_color=BACKGROUND_COLOR, end_color=BACKGROUND_COLOR, fill_type="solid")

    # Text styles
    text_font = Font(color=TEXT_FONT_COLOR)
    header_font = Font(color=HEADER_FONT_COLOR, bold=True)

    # Border styles
    thin_side = Side(border_style="thin", color=BORDER_COLOR)
    thick_side = Side(border_style="thick", color=BORDER_COLOR)

    # Make all cells have the background color
    for row in ws.iter_rows(min_row=1, max_row=bg_max_row, min_col=1, max_col=bg_max_col):
        for cell in row:
            cell.fill = black_fill

    # Header row (row 1)
    for row in ws.iter_rows(min_row=1, max_row=1, min_col=1, max_col=max_used_col):
        for i, cell in enumerate(row, start=1):
            cell.fill = header_fill
            cell.font = header_font

            # Outside border: thick on outer edges, thin between header cells.
            right_side = thick_side if i in [1, max_used_col] else thin_side
            cell.border = Border(top=thick_side, bottom=thick_side, left=thin_side, right=right_side)

    # Data rows (2..max_row)
    for idx, row in enumerate(ws.iter_rows(min_row=2, max_row=max_used_row, max_col=max_used_col), start=2):
        # Alternate row fill colors
        fill = row_fill_primary if (idx % 2 == 0) else row_fill_alt

        # Apply styles to each cell in the row
        for i, cell in enumerate(row, start=1):
            # Fill and font
            cell.fill = fill
            cell.font = text_font

            # Outside border: thick on outer edges, thin between cells.
            right_side = thick_side if i in [1, max_used_col] else thin_side
            cell.border = Border(top=thin_side, bottom=thin_side, left=thin_side, right=right_side)

def autofit_columns(ws: Worksheet, min_width: float = 8.0, max_width: float = 40.0) -> None:
    """
    Approximate auto-fit: set each column width based on the longest cell value
    (in characters) in that column, clamped between min_width and max_width.
    """
    for col_idx in range(1, ws.max_column + 1):
        column_letter = get_column_letter(col_idx)
        max_length = 0

        for row_idx in range(1, ws.max_row + 1):
            cell = ws.cell(row=row_idx, column=col_idx)
            value = cell.value
            if value is None:
                continue

            text = str(value)
            length = len(text)
            if length > max_length:
                max_length = length

        adjusted_width = max_length + 2  # some padding

        if adjusted_width < min_width:
            adjusted_width = min_width
        if adjusted_width > max_width:
            adjusted_width = max_width

        ws.column_dimensions[column_letter].width = adjusted_width
