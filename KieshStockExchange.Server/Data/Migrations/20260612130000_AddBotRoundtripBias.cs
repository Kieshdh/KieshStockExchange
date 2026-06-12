using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KieshStockExchange.Server.Data.Migrations
{
    /// <summary>Round 2 §0012 (extension E5): adds the RoundtripBiasPrc column to AIUsers. The
    /// per-bot preference for round-trip vs flip when both sizings are possible. Default 0.5
    /// (neutral); per-strategy seed values come from Tools/Config.py. Inert when
    /// Bots:Advanced:BracketFlip is off — round 2 patch 0006 reads the column only inside the
    /// _bracketFlip-on path of BuildBracketAsync.</summary>
    public partial class AddBotRoundtripBias : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "RoundtripBiasPrc",
                table: "AIUsers",
                type: "numeric(20,10)",
                nullable: false,
                defaultValue: 0.5m);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "RoundtripBiasPrc",
                table: "AIUsers");
        }
    }
}
