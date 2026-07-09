using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KieshStockExchange.Server.data.Migrations
{
    /// <inheritdoc />
    public partial class AddStockSector : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Sector",
                table: "Stocks",
                type: "text",
                nullable: false,
                defaultValue: "");

            // Populate the sector for the fixed 50-stock universe by StockId (council 5/5, 2026-07-09).
            // Activates real sectors on an EXISTING (already-seeded) DB — e.g. prod — WITHOUT a reseed. On a
            // fresh/empty DB it is a no-op (no stock rows yet) and ExcelSeedService seeds sectors from the xlsx
            // (the authoritative path). Keyed by the stable StockId ⇒ order-independent + idempotent.
            migrationBuilder.Sql(@"UPDATE ""Stocks"" SET ""Sector"" = 'Semiconductors'          WHERE ""StockId"" IN (2,7,9,27,41,45);");
            migrationBuilder.Sql(@"UPDATE ""Stocks"" SET ""Sector"" = 'Software & IT'            WHERE ""StockId"" IN (1,3,15,26,32,36,38,42,43,46);");
            migrationBuilder.Sql(@"UPDATE ""Stocks"" SET ""Sector"" = 'Communication & Internet' WHERE ""StockId"" IN (5,6,21,33);");
            migrationBuilder.Sql(@"UPDATE ""Stocks"" SET ""Sector"" = 'Consumer Discretionary'   WHERE ""StockId"" IN (4,8,19,23,34,47);");
            migrationBuilder.Sql(@"UPDATE ""Stocks"" SET ""Sector"" = 'Consumer Staples'         WHERE ""StockId"" IN (10,12,20,22,29,31);");
            migrationBuilder.Sql(@"UPDATE ""Stocks"" SET ""Sector"" = 'Health Care'              WHERE ""StockId"" IN (11,18,25,35,39,40,44,49);");
            migrationBuilder.Sql(@"UPDATE ""Stocks"" SET ""Sector"" = 'Financials'               WHERE ""StockId"" IN (13,14,16,24,30,50);");
            migrationBuilder.Sql(@"UPDATE ""Stocks"" SET ""Sector"" = 'Energy & Industrials'     WHERE ""StockId"" IN (17,28,37,48);");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Sector",
                table: "Stocks");
        }
    }
}
