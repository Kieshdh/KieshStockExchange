using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KieshStockExchange.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddBotTpParams : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "TpOffsetMaxPrc",
                table: "AIUsers",
                type: "numeric(20,10)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "TpOffsetMinPrc",
                table: "AIUsers",
                type: "numeric(20,10)",
                nullable: false,
                defaultValue: 0m);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "TpOffsetMaxPrc",
                table: "AIUsers");

            migrationBuilder.DropColumn(
                name: "TpOffsetMinPrc",
                table: "AIUsers");
        }
    }
}
