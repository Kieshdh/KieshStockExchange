using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KieshStockExchange.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddBotAdvancedProbs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "LongBracketProb",
                table: "AIUsers",
                type: "numeric(20,10)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "ShortBracketProb",
                table: "AIUsers",
                type: "numeric(20,10)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "ShortProb",
                table: "AIUsers",
                type: "numeric(20,10)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "StopProb",
                table: "AIUsers",
                type: "numeric(20,10)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "TrailingProb",
                table: "AIUsers",
                type: "numeric(20,10)",
                nullable: false,
                defaultValue: 0m);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LongBracketProb",
                table: "AIUsers");

            migrationBuilder.DropColumn(
                name: "ShortBracketProb",
                table: "AIUsers");

            migrationBuilder.DropColumn(
                name: "ShortProb",
                table: "AIUsers");

            migrationBuilder.DropColumn(
                name: "StopProb",
                table: "AIUsers");

            migrationBuilder.DropColumn(
                name: "TrailingProb",
                table: "AIUsers");
        }
    }
}
