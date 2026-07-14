using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KieshStockExchange.Server.data.Migrations
{
    /// <inheritdoc />
    public partial class AddCandleMoodBands : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<double>(
                name: "MoodMid",
                table: "Candles",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "MoodSlow",
                table: "Candles",
                type: "double precision",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "MoodMid",
                table: "Candles");

            migrationBuilder.DropColumn(
                name: "MoodSlow",
                table: "Candles");
        }
    }
}
