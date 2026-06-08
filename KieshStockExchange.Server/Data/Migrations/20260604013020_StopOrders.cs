using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KieshStockExchange.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class StopOrders : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "StopPrice",
                table: "Orders",
                type: "numeric(20,10)",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "StopPrice",
                table: "Orders");
        }
    }
}
