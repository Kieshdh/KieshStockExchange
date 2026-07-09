using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KieshStockExchange.Server.data.Migrations
{
    /// <inheritdoc />
    public partial class AddArmedStopPartialIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_Orders_ArmedStandalone_User_Stock_Side",
                table: "Orders",
                columns: new[] { "UserId", "StockId", "Side" },
                filter: "\"Status\" = 'Pending' AND \"Stop\" <> 'None' AND \"ParentOrderId\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Orders_ArmedStop_User",
                table: "Orders",
                column: "UserId",
                filter: "\"Status\" = 'Pending' AND \"Stop\" <> 'None'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Orders_ArmedStandalone_User_Stock_Side",
                table: "Orders");

            migrationBuilder.DropIndex(
                name: "IX_Orders_ArmedStop_User",
                table: "Orders");
        }
    }
}
