using Npgsql;

namespace Babelstone.EventStore;

/// <summary>
/// One recorded compliance freeze in the instance-keyed frozen-predicate read model (ADR-PC-041
/// §Decision slots 2/5): the fold of one freeze's lifecycle (<c>AccountFrozen → AccountUnfrozen</c>),
/// flattened to family-agnostic PRIMITIVES so this storage boundary names no engine domain type
/// (the same split that keeps <see cref="AccountHoldRow"/> generic).
/// </summary>
/// <remarks>
/// <para>
/// The table is a REBUILDABLE derived cache keyed by <see cref="FreezeId"/> — the ADR-PC-041
/// idempotency/correlation key both lifecycle events of one freeze carry. While <see cref="State"/>
/// is <c>ACTIVE</c> the instance is frozen and the stages-3–5 authorization decider refuses its
/// debits, naming <see cref="FreezeReason"/>/<see cref="ComplianceActor"/>. The row is
/// state-transitioned, never deleted, so "what freezes has this instance carried" stays answerable.
/// </para>
/// <para>
/// No PII (ADR-PC-004): <see cref="FreezeId"/>/<see cref="InstanceId"/> are opaque structural
/// references; <see cref="FreezeReason"/>/<see cref="UnfreezeReason"/> are stable machine codes;
/// <see cref="ComplianceActor"/>/<see cref="UnfreezeActor"/> are operator identities.
/// </para>
/// </remarks>
/// <param name="FreezeId">The freeze's lifecycle idempotency/correlation key (ADR-PC-041 slot 4).</param>
/// <param name="InstanceId">The instance the freeze blocks — the fold key, never PII.</param>
/// <param name="FreezeReason">Why the freeze was placed — a stable machine code, never PII.</param>
/// <param name="ComplianceActor">The operator/service actor that placed the freeze — never PII.</param>
/// <param name="FreezeExpiresAt">The advisory expiry horizon (ADR-PC-023); null = open-ended.</param>
/// <param name="State"><c>ACTIVE</c> (blocking debits) or <c>LIFTED</c> — the closed lifecycle set.</param>
/// <param name="PlacedStreamId">The stream that carried the <c>AccountFrozen</c> event.</param>
/// <param name="PlacedSequence">The <c>AccountFrozen</c> event's per-stream sequence.</param>
/// <param name="LiftedStreamId">The stream that carried the <c>AccountUnfrozen</c> event; null while active.</param>
/// <param name="LiftedSequence">The <c>AccountUnfrozen</c> event's per-stream sequence; null while active.</param>
/// <param name="UnfreezeActor">The actor that lifted the freeze; null while active.</param>
/// <param name="UnfreezeReason">Why the freeze was lifted; null while active.</param>
public sealed record AccountFreezeRow(
    string FreezeId,
    Guid InstanceId,
    string FreezeReason,
    string ComplianceActor,
    DateOnly? FreezeExpiresAt,
    string State,
    Guid PlacedStreamId,
    long PlacedSequence,
    Guid? LiftedStreamId = null,
    long? LiftedSequence = null,
    string? UnfreezeActor = null,
    string? UnfreezeReason = null);

/// <summary>
/// How an unfreeze landed against the freeze set — the three-way answer whose non-normal members the
/// projector must SURFACE, never silently absorb (ADR-PC-041, mirroring <see cref="HoldReleaseResult"/>).
/// </summary>
public enum FreezeLiftResult
{
    /// <summary>The freeze was ACTIVE and is now lifted — the one normal outcome.</summary>
    Transitioned,

    /// <summary>The freeze exists but had already been lifted — a duplicate/late unfreeze. Folds as a
    /// no-op; a reconciliation signal.</summary>
    AlreadyLifted,

    /// <summary>No freeze with this id was ever placed — a fold-order error.</summary>
    NeverFrozen,
}

/// <summary>
/// The generic, family-agnostic storage boundary for the spine-owned frozen-predicate read model
/// (ADR-PC-041, migration 0022). The stages-3–5 authorization decider consults
/// <see cref="GetActiveFreezeAsync"/> before its funds check; the freeze set itself is a rebuildable
/// fold of the two lifecycle events, never a stored source of truth. Family-agnostic by construction
/// — it stores only <see cref="AccountFreezeRow"/> primitives (ENGINE_FAMILY_AGNOSTIC, ADR-PC-021).
/// </summary>
public interface IAccountFreezeStore
{
    /// <summary>
    /// Record a placed freeze, idempotently on <c>freeze_id</c>: a row whose id already exists is
    /// left untouched, so a re-delivered <c>AccountFrozen</c> never freezes twice.
    /// </summary>
    Task FreezeAsync(AccountFreezeRow freeze, CancellationToken ct = default);

    /// <summary>
    /// Transition an ACTIVE freeze to LIFTED, recording the lifting event's identity and its
    /// actor/reason. A freeze not currently active transitions nothing; the returned
    /// <see cref="FreezeLiftResult"/> says which no-op it was so the caller can surface it.
    /// </summary>
    Task<FreezeLiftResult> UnfreezeAsync(
        string freezeId, Guid liftedStreamId, long liftedSequence, string unfreezeActor,
        string unfreezeReason, CancellationToken ct = default);

    /// <summary>
    /// The instance's currently-ACTIVE freeze, or null if it is not frozen (ADR-PC-041 slot 2) — the
    /// predicate the authorization decider reads. If more than one active freeze somehow exists, the
    /// earliest-placed is returned (deterministic); the "why" surfaces from it.
    /// </summary>
    Task<AccountFreezeRow?> GetActiveFreezeAsync(Guid instanceId, CancellationToken ct = default);

    /// <summary>
    /// The cross-instance freeze expiry-horizon read (ADR-PC-023): every ACTIVE freeze whose
    /// <c>freeze_expires_at</c> is non-null and at or before <paramref name="expiryHorizon"/> — what
    /// the operator/command shell reads to decide which <c>AccountUnfrozen</c> facts to append.
    /// Open-ended freezes are never candidates. The horizon is an INPUT, never a clock read.
    /// </summary>
    Task<IReadOnlyList<AccountFreezeRow>> GetActiveFreezesWithExpiryAtOrBeforeAsync(
        DateOnly expiryHorizon, CancellationToken ct = default);

    /// <summary>Truncate the whole freeze set for a clean rebuild (truncate-then-refold, ADR-PC-041).</summary>
    Task TruncateAsync(CancellationToken ct = default);
}

/// <summary>
/// PostgreSQL-backed <see cref="IAccountFreezeStore"/>. Hand-rolled, Npgsql-only, all
/// <c>account_freezes</c> SQL private to this type — the storage-boundary discipline of
/// <see cref="PostgresAccountHoldStore"/> applied to the frozen-predicate read model (migration 0022).
/// The idempotent placement is <c>INSERT … ON CONFLICT DO NOTHING</c> on <c>freeze_id</c>; the lift is
/// a single <c>UPDATE … WHERE state = 'ACTIVE'</c> transition, so a duplicate unfreeze affects zero rows.
/// </summary>
public sealed class PostgresAccountFreezeStore(string connectionString) : IAccountFreezeStore
{
    public async Task FreezeAsync(AccountFreezeRow freeze, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(freeze);

        const string sql = """
            INSERT INTO account_freezes
                (freeze_id, instance_id, freeze_reason, compliance_actor, freeze_expires_at, state,
                 placed_stream_id, placed_sequence)
            VALUES
                (@freeze_id, @instance_id, @freeze_reason, @compliance_actor, @freeze_expires_at, 'ACTIVE',
                 @placed_stream_id, @placed_sequence)
            ON CONFLICT (freeze_id) DO NOTHING;
            """;

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("freeze_id", freeze.FreezeId);
        command.Parameters.AddWithValue("instance_id", freeze.InstanceId);
        command.Parameters.AddWithValue("freeze_reason", freeze.FreezeReason);
        command.Parameters.AddWithValue("compliance_actor", freeze.ComplianceActor);
        command.Parameters.AddWithValue("freeze_expires_at", (object?)freeze.FreezeExpiresAt ?? DBNull.Value);
        command.Parameters.AddWithValue("placed_stream_id", freeze.PlacedStreamId);
        command.Parameters.AddWithValue("placed_sequence", freeze.PlacedSequence);
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<FreezeLiftResult> UnfreezeAsync(
        string freezeId, Guid liftedStreamId, long liftedSequence, string unfreezeActor,
        string unfreezeReason, CancellationToken ct = default)
    {
        const string sql = """
            UPDATE account_freezes
            SET state = 'LIFTED',
                lifted_stream_id = @lifted_stream_id,
                lifted_sequence = @lifted_sequence,
                unfreeze_actor = @unfreeze_actor,
                unfreeze_reason = @unfreeze_reason
            WHERE freeze_id = @freeze_id AND state = 'ACTIVE';
            """;

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using (var command = new NpgsqlCommand(sql, connection))
        {
            command.Parameters.AddWithValue("freeze_id", freezeId);
            command.Parameters.AddWithValue("lifted_stream_id", liftedStreamId);
            command.Parameters.AddWithValue("lifted_sequence", liftedSequence);
            command.Parameters.AddWithValue("unfreeze_actor", unfreezeActor);
            command.Parameters.AddWithValue("unfreeze_reason", unfreezeReason);
            if (await command.ExecuteNonQueryAsync(ct) == 1)
            {
                return FreezeLiftResult.Transitioned;
            }
        }

        // A zero-row unfreeze is one of two facts: the freeze exists but was already lifted, or it
        // was never placed (a fold-order error). One existence probe tells them apart.
        const string existsSql = "SELECT EXISTS (SELECT 1 FROM account_freezes WHERE freeze_id = @freeze_id);";
        await using var existsCommand = new NpgsqlCommand(existsSql, connection);
        existsCommand.Parameters.AddWithValue("freeze_id", freezeId);
        return (bool)(await existsCommand.ExecuteScalarAsync(ct))!
            ? FreezeLiftResult.AlreadyLifted
            : FreezeLiftResult.NeverFrozen;
    }

    public async Task<AccountFreezeRow?> GetActiveFreezeAsync(Guid instanceId, CancellationToken ct = default)
    {
        const string sql = """
            SELECT freeze_id, instance_id, freeze_reason, compliance_actor, freeze_expires_at, state,
                   placed_stream_id, placed_sequence, lifted_stream_id, lifted_sequence,
                   unfreeze_actor, unfreeze_reason
            FROM account_freezes
            WHERE instance_id = @instance_id AND state = 'ACTIVE'
            ORDER BY placed_sequence, freeze_id
            LIMIT 1;
            """;

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("instance_id", instanceId);
        var rows = await ReadRowsAsync(command, ct);
        return rows.Count == 0 ? null : rows[0];
    }

    public async Task<IReadOnlyList<AccountFreezeRow>> GetActiveFreezesWithExpiryAtOrBeforeAsync(
        DateOnly expiryHorizon, CancellationToken ct = default)
    {
        const string sql = """
            SELECT freeze_id, instance_id, freeze_reason, compliance_actor, freeze_expires_at, state,
                   placed_stream_id, placed_sequence, lifted_stream_id, lifted_sequence,
                   unfreeze_actor, unfreeze_reason
            FROM account_freezes
            WHERE state = 'ACTIVE'
                  AND freeze_expires_at IS NOT NULL AND freeze_expires_at <= @expiry_horizon
            ORDER BY instance_id, freeze_id;
            """;

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("expiry_horizon", expiryHorizon);
        return await ReadRowsAsync(command, ct);
    }

    public async Task TruncateAsync(CancellationToken ct = default)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var command = new NpgsqlCommand("TRUNCATE TABLE account_freezes;", connection);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task<IReadOnlyList<AccountFreezeRow>> ReadRowsAsync(
        NpgsqlCommand command, CancellationToken ct)
    {
        var freezes = new List<AccountFreezeRow>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            freezes.Add(new AccountFreezeRow(
                FreezeId: reader.GetString(0),
                InstanceId: reader.GetGuid(1),
                FreezeReason: reader.GetString(2),
                ComplianceActor: reader.GetString(3),
                FreezeExpiresAt: reader.IsDBNull(4) ? null : reader.GetFieldValue<DateOnly>(4),
                State: reader.GetString(5),
                PlacedStreamId: reader.GetGuid(6),
                PlacedSequence: reader.GetInt64(7),
                LiftedStreamId: reader.IsDBNull(8) ? null : reader.GetGuid(8),
                LiftedSequence: reader.IsDBNull(9) ? null : reader.GetInt64(9),
                UnfreezeActor: reader.IsDBNull(10) ? null : reader.GetString(10),
                UnfreezeReason: reader.IsDBNull(11) ? null : reader.GetString(11)));
        }

        return freezes;
    }
}
