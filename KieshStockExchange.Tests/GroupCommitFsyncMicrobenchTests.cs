using System.Diagnostics;
using Npgsql;
using Xunit.Abstractions;

namespace KieshStockExchange.Tests;

/// <summary>
/// GATE 0.1 — the empirical kill-check for decision/commit decoupling (group-commit
/// write-behind). Before any engine surgery we must know, on the ACTUAL docker
/// Postgres, whether coalescing many commits into one fsync is real — and whether it
/// survives <c>synchronous_commit=off</c> (already approved for prod). If group-commit
/// does not materially beat per-statement commits, OR <c>sync_commit=off</c> already
/// flattens the gap, the premise is dead and Slice 1 should NOT be built.
///
/// This is a measurement, not a behavioral test: it self-skips unless
/// <c>KSE_FSYNC_BENCH=1</c> so the normal <c>dotnet test</c> run (no DB) stays green.
/// Run it explicitly against docker PG (see the bake runbook):
/// <code>
/// $env:KSE_FSYNC_BENCH='1'
/// $env:KSE_DB_CONNECTION_STRING='Host=localhost;Port=5432;Database=kse;Username=kse;Password=kse-dev'
/// dotnet test --filter FullyQualifiedName~GroupCommitFsyncMicrobench -l "console;verbosity=detailed"
/// </code>
/// It opens ONE physical connection (<c>Maximum Pool Size=1</c>) and, for each of
/// { synchronous_commit=on, off }, times: (A) N separate BEGIN/INSERT/COMMIT
/// round-trips, (B) N inserts under ONE COMMIT, and (C) the same N inserts as a single
/// pipelined <see cref="NpgsqlBatch"/> under one COMMIT. It prints commits/sec,
/// inserts/sec and the A→B/C coalescing speedup, then leaves the verdict to the human
/// gate in the runbook.
/// </summary>
public sealed class GroupCommitFsyncMicrobenchTests
{
    private readonly ITestOutputHelper _out;

    public GroupCommitFsyncMicrobenchTests(ITestOutputHelper output) => _out = output;

    private const string DefaultConn =
        "Host=localhost;Port=5432;Database=kse;Username=kse;Password=kse-dev";
    private const string Table = "kse_bench_fsync";

    [Fact]
    public async Task FsyncMicrobench_GroupCommit_vs_SeparateCommits()
    {
        if (Environment.GetEnvironmentVariable("KSE_FSYNC_BENCH") != "1")
        {
            _out.WriteLine("KSE_FSYNC_BENCH != 1 — skipping fsync microbench (set it to 1 to run against docker PG).");
            return; // self-skip: keeps the DB-less suite green
        }

        var baseConn = Environment.GetEnvironmentVariable("KSE_DB_CONNECTION_STRING");
        if (string.IsNullOrWhiteSpace(baseConn)) baseConn = DefaultConn;
        int n = ReadIntEnv("KSE_FSYNC_BENCH_N", 2000);

        // ONE physical connection — we are measuring per-connection commit/fsync cost,
        // not pool concurrency. A WAL-logged table is required: TEMP/UNLOGGED tables
        // skip the WAL flush and would void the measurement.
        var csb = new NpgsqlConnectionStringBuilder(baseConn) { MaxPoolSize = 1, MinPoolSize = 1 };

        await using var conn = new NpgsqlConnection(csb.ConnectionString);
        await conn.OpenAsync();

        await Exec(conn, $"CREATE TABLE IF NOT EXISTS {Table} (id integer, payload text)");
        try
        {
            _out.WriteLine($"fsync microbench: N={n} inserts, one connection, table={Table}");
            _out.WriteLine("regime              | A separate commits/sec | B 1-commit inserts/sec | C batch inserts/sec | B/A speedup | C/A speedup");
            _out.WriteLine("--------------------+------------------------+------------------------+---------------------+-------------+------------");

            var on  = await RunRegime(conn, n, syncCommitOff: false);
            var off = await RunRegime(conn, n, syncCommitOff: true);

            _out.WriteLine("");
            _out.WriteLine("INTERPRETATION (apply the runbook kill/proceed gate):");
            _out.WriteLine($"  - Coalescing real?  B/A speedup with sync_commit=on  = {on.SpeedupB:F1}x  (>~ a few x ⇒ fsync coalescing is real).");
            _out.WriteLine($"  - Survives sc=off?  B/A speedup with sync_commit=off = {off.SpeedupB:F1}x  (≈1x ⇒ sc=off already deleted the fsync cost ⇒ premise dead).");
            _out.WriteLine($"  - sc=off lift on A (per-commit path): {(on.CommitsPerSecA > 0 ? off.CommitsPerSecA / on.CommitsPerSecA : 0):F1}x faster separate-commit throughput.");

            // Soft sanity only — this is a measurement harness, the human gate decides.
            Assert.True(on.CommitsPerSecA > 0 && off.CommitsPerSecA > 0, "microbench produced no timing");
        }
        finally
        {
            await Exec(conn, $"DROP TABLE IF EXISTS {Table}");
        }
    }

    private readonly record struct RegimeResult(
        double CommitsPerSecA, double InsertsPerSecB, double InsertsPerSecC, double SpeedupB, double SpeedupC);

    private async Task<RegimeResult> RunRegime(NpgsqlConnection conn, int n, bool syncCommitOff)
    {
        await Exec(conn, syncCommitOff ? "SET synchronous_commit = off" : "SET synchronous_commit = on");
        await Exec(conn, $"TRUNCATE {Table}");

        // Warm up (JIT, plan cache, connection) so the first regime isn't penalised.
        await ModeA_SeparateCommits(conn, Math.Min(100, n));
        await Exec(conn, $"TRUNCATE {Table}");

        var swA = Stopwatch.StartNew();
        await ModeA_SeparateCommits(conn, n);
        swA.Stop();
        await Exec(conn, $"TRUNCATE {Table}");

        var swB = Stopwatch.StartNew();
        await ModeB_OneCommit(conn, n);
        swB.Stop();
        await Exec(conn, $"TRUNCATE {Table}");

        var swC = Stopwatch.StartNew();
        await ModeC_BatchOneCommit(conn, n);
        swC.Stop();

        double secA = swA.Elapsed.TotalSeconds, secB = swB.Elapsed.TotalSeconds, secC = swC.Elapsed.TotalSeconds;
        double cpsA = secA > 0 ? n / secA : 0;   // A: 1 commit per insert ⇒ commits/sec == inserts/sec
        double ipsB = secB > 0 ? n / secB : 0;
        double ipsC = secC > 0 ? n / secC : 0;
        double spB = cpsA > 0 ? ipsB / cpsA : 0;
        double spC = cpsA > 0 ? ipsC / cpsA : 0;

        string label = syncCommitOff ? "synchronous_commit=off" : "synchronous_commit=on ";
        _out.WriteLine($"{label} | {cpsA,22:N0} | {ipsB,22:N0} | {ipsC,19:N0} | {spB,10:F1}x | {spC,10:F1}x");

        return new RegimeResult(cpsA, ipsB, ipsC, spB, spC);
    }

    // Mode A: N independent transactions — N WAL flushes (N fsync round-trips).
    private static async Task ModeA_SeparateCommits(NpgsqlConnection conn, int n)
    {
        for (int i = 0; i < n; i++)
        {
            await using var tx = await conn.BeginTransactionAsync();
            await using var cmd = new NpgsqlCommand($"INSERT INTO {Table}(id, payload) VALUES (@id, 'x')", conn, tx);
            cmd.Parameters.AddWithValue("id", i);
            await cmd.ExecuteNonQueryAsync();
            await tx.CommitAsync();
        }
    }

    // Mode B: N INSERT statements inside ONE transaction — N WAL records, ONE flush at COMMIT.
    private static async Task ModeB_OneCommit(NpgsqlConnection conn, int n)
    {
        await using var tx = await conn.BeginTransactionAsync();
        await using var cmd = new NpgsqlCommand($"INSERT INTO {Table}(id, payload) VALUES (@id, 'x')", conn, tx);
        var p = cmd.Parameters.Add(new NpgsqlParameter("id", NpgsqlTypes.NpgsqlDbType.Integer));
        await cmd.PrepareAsync();
        for (int i = 0; i < n; i++)
        {
            p.Value = i;
            await cmd.ExecuteNonQueryAsync();
        }
        await tx.CommitAsync();
    }

    // Mode C: the same N inserts as a single pipelined NpgsqlBatch under one COMMIT —
    // also collapses the per-statement network round-trips, not just the fsync.
    private static async Task ModeC_BatchOneCommit(NpgsqlConnection conn, int n)
    {
        await using var tx = await conn.BeginTransactionAsync();
        await using var batch = new NpgsqlBatch(conn, tx);
        for (int i = 0; i < n; i++)
            batch.BatchCommands.Add(new NpgsqlBatchCommand($"INSERT INTO {Table}(id, payload) VALUES ({i}, 'x')"));
        await batch.ExecuteNonQueryAsync();
        await tx.CommitAsync();
    }

    private static async Task Exec(NpgsqlConnection conn, string sql)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();
    }

    private static int ReadIntEnv(string name, int fallback)
        => int.TryParse(Environment.GetEnvironmentVariable(name), out var v) && v > 0 ? v : fallback;
}
