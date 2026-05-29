using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KieshStockExchange.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class DropDeadAiUserColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "MaxDailyTrades",
                table: "AIUsers");

            migrationBuilder.DropColumn(
                name: "MaxOpenPositions",
                table: "AIUsers");

            migrationBuilder.DropColumn(
                name: "MinOpenPositions",
                table: "AIUsers");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "MaxDailyTrades",
                table: "AIUsers",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "MaxOpenPositions",
                table: "AIUsers",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "MinOpenPositions",
                table: "AIUsers",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }
    }
}
