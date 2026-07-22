using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KieshStockExchange.Server.data.Migrations
{
    /// <inheritdoc />
    public partial class AddAdminTableTimeIndexes : Migration
    {
        // Built CONCURRENTLY so creating these on the large live Orders/Transactions tables never
        // takes an ACCESS EXCLUSIVE lock (reads/writes keep flowing). CONCURRENTLY can't run inside
        // a transaction, so each statement is emitted with suppressTransaction: true and the whole
        // migration runs outside the migration transaction. IF NOT EXISTS makes re-runs safe.
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                "CREATE INDEX CONCURRENTLY IF NOT EXISTS \"IX_Tx_Timestamp\" ON \"Transactions\" (\"Timestamp\");",
                suppressTransaction: true);

            migrationBuilder.Sql(
                "CREATE INDEX CONCURRENTLY IF NOT EXISTS \"IX_Orders_CreatedAt\" ON \"Orders\" (\"CreatedAt\");",
                suppressTransaction: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                "DROP INDEX CONCURRENTLY IF EXISTS \"IX_Tx_Timestamp\";",
                suppressTransaction: true);

            migrationBuilder.Sql(
                "DROP INDEX CONCURRENTLY IF EXISTS \"IX_Orders_CreatedAt\";",
                suppressTransaction: true);
        }
    }
}
