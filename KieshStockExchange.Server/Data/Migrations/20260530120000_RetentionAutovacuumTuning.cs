using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KieshStockExchange.Server.Data.Migrations
{
    /// <summary>
    /// Wave 8 §3 — tune per-table autovacuum on the two high-churn tables so that,
    /// once retention deletes ≈ inserts, dead tuples are reclaimed aggressively and
    /// the tables stop growing on disk (reusing freed space). No model/schema change;
    /// the snapshot is unchanged. Returning space to the OS still needs a manual
    /// VACUUM FULL / pg_repack (see deploy/RUNBOOK.md).
    /// </summary>
    public partial class RetentionAutovacuumTuning : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                @"ALTER TABLE ""Orders"" SET (autovacuum_vacuum_scale_factor = 0.02, autovacuum_vacuum_cost_limit = 2000);");
            migrationBuilder.Sql(
                @"ALTER TABLE ""Transactions"" SET (autovacuum_vacuum_scale_factor = 0.02, autovacuum_vacuum_cost_limit = 2000);");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                @"ALTER TABLE ""Orders"" RESET (autovacuum_vacuum_scale_factor, autovacuum_vacuum_cost_limit);");
            migrationBuilder.Sql(
                @"ALTER TABLE ""Transactions"" RESET (autovacuum_vacuum_scale_factor, autovacuum_vacuum_cost_limit);");
        }
    }
}
