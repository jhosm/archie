using Npgsql;

namespace Babelstone.EventStore;

/// <summary>
/// Per-table retention windows for the two dedup ledgers (the §4 "bounded retention window is an
/// implementation detail" of ADR-PC-029). The windows are NOT one size: the command ledger's window
/// is load-bearing for correctness (pruning a receipt too early can open a DUPLICATE deposit), the
/// inbox's is the simpler Kafka-retention × N (Document 04). Both default conservatively.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why the command window is so long.</b> ADR-PC-029 §4 fixes the uniqueness window at "at least
/// the stream's active lifetime". A <c>command_dedup</c> receipt is the ONLY thing that turns a late,
/// at-least-once retry of an already-applied command into a no-op replay. Prune it while the same
/// command could still be retried and the retry either (a) re-executes and collides on
/// <c>events_stream_seq_uq</c> → a spurious 409 for a deterministic deposit id, or (b) WORSE, opens a
/// SECOND deposit for a server-generated deposit id. So the window must exceed BOTH the dispatcher's
/// max retry horizon AND the stream's active lifetime. The dispatcher's retry horizon is bounded
/// (<c>SagaCommandDispatcherService</c> backs off to a 30s ceiling and a 4xx is terminal-no-retry),
/// so the binding term is the stream lifetime: the longest v1 term-deposit product is 24 months
/// (<c>dpz_pt_24m_…</c>, term_days 730), and an auto-renewal chains a fresh stream rather than
/// extending one, so 24 months bounds a single stream's active life. The default adds generous
/// head-room over that — <b>1095 days (3 years)</b> — so a receipt always outlives any command that
/// could still target its stream, with margin for clock skew and the longest term plus a renewal
/// boundary. This is a retention floor, not a tuning knob to shorten casually.
/// </para>
/// <para>
/// <b>Why the inbox window is short.</b> The inbox dedupes PHYSICAL message re-deliveries; a redelivery
/// can only happen within the broker's retention horizon. Document 04 sizes the inbox sweep at Kafka
/// retention × N (typically 7–30 days). The default is <b>30 days</b> — comfortably past the dev
/// Redpanda 7-day retention and any consumer-lag replay, with no correctness coupling to a stream's
/// lifetime (a re-delivery past 30 days cannot exist, because the message is gone from the log).
/// </para>
/// <para>
/// Both ledgers carry only structural ids / sequences, never PII (ADR-PC-004 §P2); retention here is
/// disk-bounding + GDPR data-minimization hygiene, not a PII-erasure obligation.
/// </para>
/// </remarks>
public sealed record DedupRetentionOptions
{
    /// <summary>PostgreSQL connection string for the engine database holding the dedup ledgers.</summary>
    public required string ConnectionString { get; init; }

    /// <summary>
    /// How long a <c>command_dedup</c> receipt is kept (ADR-PC-029 §4). MUST exceed the stream's
    /// active lifetime AND the dispatcher's max retry horizon — default 1095 days (3 years), a floor.
    /// </summary>
    public TimeSpan CommandDedupRetention { get; init; } = TimeSpan.FromDays(1095);

    /// <summary>
    /// How long an <c>inbox</c> dedup row is kept (Document 04: Kafka retention × N) — default 30 days.
    /// </summary>
    public TimeSpan InboxRetention { get; init; } = TimeSpan.FromDays(30);

    /// <summary>
    /// Max rows deleted per table per sweep cycle, so a first sweep over a large backlog stays a bounded,
    /// index-only range-delete that never holds a long lock or a huge transaction. The loop keeps sweeping
    /// while a cycle hits the cap (there is still a backlog) and idles for <see cref="SweepInterval"/> once
    /// a cycle deletes fewer than the cap (the tail is caught up).
    /// </summary>
    public int BatchSize { get; init; } = 10_000;

    /// <summary>Interval between sweeps once the tail is caught up (default 6h — a retention sweep is
    /// a slow housekeeping job, not a hot loop; the window is days, so sub-day cadence is ample).</summary>
    public TimeSpan SweepInterval { get; init; } = TimeSpan.FromHours(6);
}

/// <summary>
/// The retention sweep for the engine's two dedup ledgers — <c>command_dedup</c> (migration 0015,
/// command-ingress idempotency) and <c>inbox</c> (migration 0012, event-consume dedup). Each ledger
/// grows one row per command/message forever; both migrations shipped the SEAM for this sweep (the
/// <c>created_at</c>/<c>processed_at</c> btree index, the <c>GRANT … DELETE</c> to the runtime role)
/// but NOT the job. This is the job: a single <see cref="SweepOnceAsync"/> call deletes the aged tail
/// of BOTH tables, bounded to <see cref="DedupRetentionOptions.BatchSize"/> rows per table.
/// </summary>
/// <remarks>
/// Npgsql-only, alongside <see cref="PostgresCommandLog"/>, in the single assembly that owns the
/// engine's storage tables. The age cutoff is computed IN THE DATABASE (<c>created_at &lt; now() -
/// @retention</c>) so host/DB clock skew cannot bias which rows age out — the same single-clock
/// discipline the outbox lag SLI uses. The delete is keyed on the indexed timestamp column, so it is a
/// cheap range scan of the tail, and the <c>ctid IN (SELECT … LIMIT n)</c> shape caps each statement.
/// </remarks>
public sealed class DedupRetentionSweeper(DedupRetentionOptions options)
{
    /// <summary>
    /// Sweeps one batch from each ledger: deletes up to <see cref="DedupRetentionOptions.BatchSize"/>
    /// rows older than each table's window. Returns the per-table counts so the caller can decide
    /// whether a backlog remains (a full batch ⇒ keep sweeping) or the tail is caught up.
    /// </summary>
    public async Task<DedupSweepResult> SweepOnceAsync(CancellationToken ct = default)
    {
        await using var connection = new NpgsqlConnection(options.ConnectionString);
        await connection.OpenAsync(ct);

        var commandDeleted = await DeleteAgedAsync(
            connection,
            table: "command_dedup",
            ageColumn: "created_at",
            retention: options.CommandDedupRetention,
            ct);

        var inboxDeleted = await DeleteAgedAsync(
            connection,
            table: "inbox",
            ageColumn: "processed_at",
            retention: options.InboxRetention,
            ct);

        return new DedupSweepResult(commandDeleted, inboxDeleted);
    }

    /// <summary>
    /// Deletes up to <see cref="DedupRetentionOptions.BatchSize"/> rows of <paramref name="table"/>
    /// whose <paramref name="ageColumn"/> is older than <paramref name="retention"/>, measured against
    /// the DATABASE clock (<c>now()</c>). The bound is applied by selecting the oldest aged <c>ctid</c>s
    /// (the indexed range scan) and deleting exactly those, so the statement never deletes more than the
    /// cap in one transaction. The table/column names are sweeper-internal literals (never user input),
    /// so the interpolation carries no injection surface.
    /// </summary>
    private async Task<int> DeleteAgedAsync(
        NpgsqlConnection connection, string table, string ageColumn, TimeSpan retention, CancellationToken ct)
    {
        var sql =
            $"""
            DELETE FROM {table}
            WHERE ctid IN (
                SELECT ctid FROM {table}
                WHERE {ageColumn} < now() - @retention
                ORDER BY {ageColumn}
                LIMIT @batch_size
            );
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("retention", retention);
        command.Parameters.AddWithValue("batch_size", options.BatchSize);
        return await command.ExecuteNonQueryAsync(ct);
    }
}

/// <summary>The rows deleted from each ledger in one sweep cycle.</summary>
/// <param name="CommandDedupDeleted">Aged <c>command_dedup</c> receipts deleted this cycle.</param>
/// <param name="InboxDeleted">Aged <c>inbox</c> dedup rows deleted this cycle.</param>
public readonly record struct DedupSweepResult(int CommandDedupDeleted, int InboxDeleted)
{
    /// <summary>The total rows deleted across both ledgers this cycle.</summary>
    public int Total => CommandDedupDeleted + InboxDeleted;
}
