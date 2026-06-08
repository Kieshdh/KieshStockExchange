using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KieshStockExchange.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class DecomposeOrderType : Migration
    {
        // §3.6 decomposition: replace the flat OrderType string column with three orthogonal
        // dimension columns (Side/Entry/Stop) + the trailing schema. The CASE data-migration is a
        // safety net — the deploy reseeds an empty Orders table (new bot variables → Excel regen),
        // so in practice it migrates zero rows; it stays correct if ever run against data.
        // TrueMarket vs SlippageMarket both map to Entry=Market and remain distinguished by the
        // existing SlippagePercent column (null = true market), so the split is lossless.

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 1. Add the new columns (dimensions nullable until back-filled; trailing nullable).
            migrationBuilder.AddColumn<string>(name: "Side", table: "Orders", type: "text", nullable: true);
            migrationBuilder.AddColumn<string>(name: "Entry", table: "Orders", type: "text", nullable: true);
            migrationBuilder.AddColumn<string>(name: "Stop", table: "Orders", type: "text", nullable: true);
            migrationBuilder.AddColumn<decimal>(name: "TrailOffset", table: "Orders", type: "numeric(20,10)", nullable: true);
            migrationBuilder.AddColumn<bool>(name: "TrailIsPercent", table: "Orders", type: "boolean", nullable: true);
            migrationBuilder.AddColumn<decimal>(name: "TrailWatermark", table: "Orders", type: "numeric(20,10)", nullable: true);

            // 2. Back-fill the dimensions from the flat OrderType (lossless per the mapping table).
            migrationBuilder.Sql(@"
                UPDATE ""Orders"" SET
                    ""Side""  = CASE WHEN ""OrderType"" LIKE '%Sell' THEN 'Sell' ELSE 'Buy' END,
                    ""Entry"" = CASE WHEN ""OrderType"" LIKE '%Limit%' THEN 'Limit' ELSE 'Market' END,
                    ""Stop""  = CASE WHEN ""OrderType"" LIKE 'Stop%' THEN 'Stop' ELSE 'None' END;");

            // 3. Enforce NOT NULL now that every row is populated.
            migrationBuilder.AlterColumn<string>(name: "Side", table: "Orders", type: "text", nullable: false,
                defaultValue: "Buy", oldClrType: typeof(string), oldType: "text", oldNullable: true);
            migrationBuilder.AlterColumn<string>(name: "Entry", table: "Orders", type: "text", nullable: false,
                defaultValue: "Limit", oldClrType: typeof(string), oldType: "text", oldNullable: true);
            migrationBuilder.AlterColumn<string>(name: "Stop", table: "Orders", type: "text", nullable: false,
                defaultValue: "None", oldClrType: typeof(string), oldType: "text", oldNullable: true);

            // 4. Drop the flat column — the domain exposes a computed read-only OrderType instead.
            migrationBuilder.DropColumn(name: "OrderType", table: "Orders");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(name: "OrderType", table: "Orders", type: "text", nullable: true);

            migrationBuilder.Sql(@"
                UPDATE ""Orders"" SET ""OrderType"" = CASE
                    WHEN ""Stop"" = 'Stop' AND ""Entry"" = 'Market' AND ""Side"" = 'Buy'  THEN 'StopMarketBuy'
                    WHEN ""Stop"" = 'Stop' AND ""Entry"" = 'Market' AND ""Side"" = 'Sell' THEN 'StopMarketSell'
                    WHEN ""Stop"" = 'Stop' AND ""Entry"" = 'Limit'  AND ""Side"" = 'Buy'  THEN 'StopLimitBuy'
                    WHEN ""Stop"" = 'Stop' AND ""Entry"" = 'Limit'  AND ""Side"" = 'Sell' THEN 'StopLimitSell'
                    WHEN ""Entry"" = 'Limit'  AND ""Side"" = 'Buy'  THEN 'LimitBuy'
                    WHEN ""Entry"" = 'Limit'  AND ""Side"" = 'Sell' THEN 'LimitSell'
                    WHEN ""Entry"" = 'Market' AND ""Side"" = 'Buy'  AND ""SlippagePercent"" IS NOT NULL THEN 'SlippageMarketBuy'
                    WHEN ""Entry"" = 'Market' AND ""Side"" = 'Sell' AND ""SlippagePercent"" IS NOT NULL THEN 'SlippageMarketSell'
                    WHEN ""Entry"" = 'Market' AND ""Side"" = 'Buy'  THEN 'TrueMarketBuy'
                    WHEN ""Entry"" = 'Market' AND ""Side"" = 'Sell' THEN 'TrueMarketSell'
                    ELSE 'LimitBuy' END;");

            migrationBuilder.AlterColumn<string>(name: "OrderType", table: "Orders", type: "text", nullable: false,
                defaultValue: "", oldClrType: typeof(string), oldType: "text", oldNullable: true);

            migrationBuilder.DropColumn(name: "Side", table: "Orders");
            migrationBuilder.DropColumn(name: "Entry", table: "Orders");
            migrationBuilder.DropColumn(name: "Stop", table: "Orders");
            migrationBuilder.DropColumn(name: "TrailOffset", table: "Orders");
            migrationBuilder.DropColumn(name: "TrailIsPercent", table: "Orders");
            migrationBuilder.DropColumn(name: "TrailWatermark", table: "Orders");
        }
    }
}
