using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KieshStockExchange.Server.data.Migrations
{
    /// <summary>
    /// #141: realign the EF model snapshot with the live schema. The columns Orders.FlipQuantity
    /// (R2 §0007) and AIUsers.RoundtripBiasPrc (R2 §0012) were added to every DB by two hand-written
    /// migrations (20260612120000 / 20260612130000) whose malformed casing/missing designer left them
    /// invisible to the design-time tooling, so the cumulative snapshot never recorded them and
    /// EF threw PendingModelChangesWarning at every startup (Migrate → ValidateMigrations).
    /// This migration's value is the regenerated snapshot (model == snapshot again). Its Up is written
    /// IDEMPOTENTLY (ADD COLUMN IF NOT EXISTS) so it is a safe no-op on the prod/seed DBs that already
    /// carry the columns, while still provisioning them on a fresh database. Down is intentionally a
    /// no-op: the columns predate this migration on every existing DB, so dropping them on a rollback
    /// would destroy live data.
    /// </summary>
    public partial class SyncRoundtripFlipSnapshot : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // IF NOT EXISTS: no-op where the hand-written migrations already added the column
            // (prod/seed), real on a fresh DB. Defaults match the original migrations (0 / 0.5).
            migrationBuilder.Sql(
                "ALTER TABLE \"Orders\" ADD COLUMN IF NOT EXISTS \"FlipQuantity\" integer NOT NULL DEFAULT 0;");
            migrationBuilder.Sql(
                "ALTER TABLE \"AIUsers\" ADD COLUMN IF NOT EXISTS \"RoundtripBiasPrc\" numeric(20,10) NOT NULL DEFAULT 0.5;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // No-op: the columns are owned by the earlier (20260612*) migrations on every existing DB;
            // this migration only re-synced the snapshot, so it must not drop live columns on rollback.
        }
    }
}
