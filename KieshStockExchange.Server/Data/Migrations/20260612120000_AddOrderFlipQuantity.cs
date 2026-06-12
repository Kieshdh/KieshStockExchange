using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KieshStockExchange.Server.Data.Migrations
{
    /// <summary>Round 2 §0007 (Path 2): adds the FlipQuantity column to Orders. Persists how many
    /// shares of a bracket parent's quantity opened a NEW position (flip portion) vs rolled an
    /// existing inventory portion. Every other order carries 0 — preserved by the NOT NULL DEFAULT
    /// so pre-Path-2 rows + plain orders read identically to before.</summary>
    public partial class AddOrderFlipQuantity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "FlipQuantity",
                table: "Orders",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "FlipQuantity",
                table: "Orders");
        }
    }
}
