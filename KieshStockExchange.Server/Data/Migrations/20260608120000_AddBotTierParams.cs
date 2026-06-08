using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KieshStockExchange.Server.Data.Migrations
{
    /// <summary>
    /// §P6 market balancing: per-bot tiered-limit bands (Mid/Far), protective-stop distance band, and
    /// the Far-order value budget used by the tier-aware prune. All numeric(20,10), default 0.
    /// </summary>
    /// <inheritdoc />
    public partial class AddBotTierParams : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "MidLimitMinPrc",
                table: "AIUsers",
                type: "numeric(20,10)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "MidLimitMaxPrc",
                table: "AIUsers",
                type: "numeric(20,10)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "FarLimitMinPrc",
                table: "AIUsers",
                type: "numeric(20,10)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "FarLimitMaxPrc",
                table: "AIUsers",
                type: "numeric(20,10)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "StopDistanceMinPrc",
                table: "AIUsers",
                type: "numeric(20,10)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "StopDistanceMaxPrc",
                table: "AIUsers",
                type: "numeric(20,10)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "FarBudgetPrc",
                table: "AIUsers",
                type: "numeric(20,10)",
                nullable: false,
                defaultValue: 0m);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(name: "MidLimitMinPrc", table: "AIUsers");
            migrationBuilder.DropColumn(name: "MidLimitMaxPrc", table: "AIUsers");
            migrationBuilder.DropColumn(name: "FarLimitMinPrc", table: "AIUsers");
            migrationBuilder.DropColumn(name: "FarLimitMaxPrc", table: "AIUsers");
            migrationBuilder.DropColumn(name: "StopDistanceMinPrc", table: "AIUsers");
            migrationBuilder.DropColumn(name: "StopDistanceMaxPrc", table: "AIUsers");
            migrationBuilder.DropColumn(name: "FarBudgetPrc", table: "AIUsers");
        }
    }
}
