using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KieshStockExchange.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddBotArbitrageParams : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "MinArbitrageRatePrc",
                table: "AIUsers",
                type: "numeric(20,10)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<int>(
                name: "MaxInventoryPerStock",
                table: "AIUsers",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "ConversionCadenceSeconds",
                table: "AIUsers",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "MinArbitrageRatePrc",
                table: "AIUsers");

            migrationBuilder.DropColumn(
                name: "MaxInventoryPerStock",
                table: "AIUsers");

            migrationBuilder.DropColumn(
                name: "ConversionCadenceSeconds",
                table: "AIUsers");
        }
    }
}
