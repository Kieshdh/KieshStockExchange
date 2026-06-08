using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KieshStockExchange.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddOrderActivatedAt : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "ActivatedAt",
                table: "Orders",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ActivatedAt",
                table: "Orders");
        }
    }
}
