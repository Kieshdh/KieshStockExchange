using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KieshStockExchange.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class ShortPositions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_Positions_Quantity_Invariants",
                table: "Positions");

            migrationBuilder.AddColumn<decimal>(
                name: "ShortCollateral",
                table: "Positions",
                type: "numeric(20,10)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<string>(
                name: "ShortCollateralCurrency",
                table: "Positions",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Positions_Quantity_Invariants",
                table: "Positions",
                sql: "\"ReservedQuantity\" >= 0 AND \"ReservedQuantity\" <= GREATEST(\"Quantity\", 0) AND \"ShortCollateral\" >= 0 AND (\"Quantity\" >= 0 OR \"ReservedQuantity\" = 0) AND (\"Quantity\" < 0 OR \"ShortCollateral\" = 0)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_Positions_Quantity_Invariants",
                table: "Positions");

            migrationBuilder.DropColumn(
                name: "ShortCollateral",
                table: "Positions");

            migrationBuilder.DropColumn(
                name: "ShortCollateralCurrency",
                table: "Positions");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Positions_Quantity_Invariants",
                table: "Positions",
                sql: "\"Quantity\" >= 0 AND \"ReservedQuantity\" >= 0 AND \"ReservedQuantity\" <= \"Quantity\"");
        }
    }
}
