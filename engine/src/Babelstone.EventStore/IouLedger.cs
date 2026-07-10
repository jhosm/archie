using Npgsql;

namespace Babelstone.EventStore;

/// <summary>
/// One recorded undeliverable credit in the <c>intent_id</c>-keyed IOU/escheat read model
/// (ADR-PC-043 slot 5): the fold of one undeliverable credit's lifecycle
/// (<c>CreditUnapplied → CreditReapplied</c>), flattened to family-agnostic PRIMITIVES so this
/// storage boundary names no engine domain type (the same split that keeps
/// <see cref="AccountHoldRow"/> generic — the typed <c>Babelstone.Engine</c> projector maps the
/// spine credit-events onto these columns).
/// </summary>
/// <remarks>
/// <para>
/// The table is a REBUILDABLE derived cache keyed by <see cref="IntentId"/> — the ADR-PC-043 slot-4
/// economic-intent id the CreditUnapplied carries and its resolving CreditReapplied references. While
/// <see cref="State"/> is <c>OUTSTANDING</c> the IOU is owed; a matching resolution transitions the
/// row to <c>RESOLVED</c>. The row is state-transitioned, never deleted, so "what credits has been
/// undeliverable, and which are still owed" stays answerable by query (migration 0024).
/// </para>
/// <para>
/// No PII (ADR-PC-004): <see cref="IntentId"/> / <see cref="BeneficiaryRef"/> are opaque structural
/// references (never an IBAN); <see cref="Reason"/> is a machine code; <see cref="State"/> is a
/// closed-set member name; the rest are ids, amounts, and command-supplied dates.
/// </para>
/// </remarks>
/// <param name="IntentId">The undeliverable credit's economic-intent id (ADR-PC-043 slot 4) — the
/// fold key, never PII.</param>
/// <param name="BeneficiaryRef">The opaque beneficiary the credit was owed to — never PII.</param>
/// <param name="AmountCents">The undeliverable amount, integer cents (ADR-PC-010).</param>
/// <param name="Reason">The stable machine reason code the credit was undeliverable — never PII.</param>
/// <param name="UnappliedAt">The economic date the credit was recorded unapplied — a command-supplied
/// input (ADR-PC-023), the IOU's age axis.</param>
/// <param name="State"><c>OUTSTANDING</c> (owed) or <c>RESOLVED</c> (reapplied) — the closed lifecycle set.</param>
/// <param name="UnappliedStreamId">The stream that carried the opening <c>CreditUnapplied</c>.</param>
/// <param name="UnappliedSequence">The opening event's per-stream sequence.</param>
/// <param name="ResolutionIntentId">The resolution key <c>g(IntentId)</c> the resolving
/// <c>CreditReapplied</c> carried (the double-pay guard, ADR-PC-043); null while outstanding.</param>
/// <param name="ReappliedRef">The account the reapplied credit landed on; null while outstanding.</param>
/// <param name="ReappliedAmountCents">The reapplied amount; null while outstanding.</param>
/// <param name="ReappliedAt">The economic date the credit was reapplied; null while outstanding.</param>
/// <param name="ResolvedStreamId">The stream that carried the resolving event; null while outstanding.</param>
/// <param name="ResolvedSequence">The resolving event's per-stream sequence; null while outstanding.</param>
public sealed record UndeliverableCreditRow(
    string IntentId,
    string BeneficiaryRef,
    long AmountCents,
    string Reason,
    DateOnly UnappliedAt,
    string State,
    Guid UnappliedStreamId,
    long UnappliedSequence,
    string? ResolutionIntentId = null,
    string? ReappliedRef = null,
    long? ReappliedAmountCents = null,
    DateOnly? ReappliedAt = null,
    Guid? ResolvedStreamId = null,
    long? ResolvedSequence = null);

/// <summary>
/// How a resolution (<c>CreditReapplied</c>) landed against the IOU set — the answer whose non-normal
/// member the projector must SURFACE, never silently absorb (the same posture as
/// <see cref="HoldReleaseResult"/>, ADR-PC-043).
/// </summary>
/// <remarks>
/// There is deliberately no <c>NeverUnapplied</c> member: the fold is COMMUTATIVE (the rebuild
/// -determinism fix), so a resolution folded BEFORE its open is NOT a no-op — it records a RESOLVED
/// tombstone and returns <see cref="Transitioned"/>, and a later open for that intent no-ops on the
/// conflict rather than re-opening. So the only non-normal outcome left is a DUPLICATE resolution of an
/// already-resolved intent (<see cref="AlreadyResolved"/>).
/// </remarks>
public enum CreditResolutionResult
{
    /// <summary>The IOU is now RESOLVED — either an OUTSTANDING row transitioned, or (resolve-before
    /// -open) a RESOLVED tombstone was recorded. The one normal outcome.</summary>
    Transitioned,

    /// <summary>The IOU exists but had already been resolved — a duplicate/late resolution. Folds as a
    /// no-op (never a double-pay); a reconciliation signal.</summary>
    AlreadyResolved,
}

/// <summary>
/// The generic, family-agnostic storage boundary for the spine-owned undeliverable-credit (IOU /
/// escheat) read model (ADR-PC-043 slot 5, migration 0024). An operator lists the OUTSTANDING IOUs —
/// which credits are still owed, to whom, and how old — via <see cref="GetOutstandingAsync"/>; the IOU
/// set itself is a rebuildable fold of the two lifecycle events, never a stored source of truth.
/// Family-agnostic by construction — it stores only <see cref="UndeliverableCreditRow"/> primitives,
/// so adding a family is zero diff here (ENGINE_FAMILY_AGNOSTIC, ADR-PC-021).
/// </summary>
/// <remarks>
/// <para>
/// Idempotency mirrors the lifecycle's own key (ADR-PC-043): <see cref="RecordUnappliedAsync"/> is a
/// no-op when the <c>intent_id</c> already exists (so a re-delivered <c>CreditUnapplied</c>, or an open
/// arriving after its resolution's tombstone, never re-opens the IOU), and <see cref="ResolveAsync"/>
/// resolves the intent at most once. <see cref="TruncateAsync"/> is the rebuild path
/// (truncate-then-refold).
/// </para>
/// <para>
/// <b>The fold is COMMUTATIVE (the rebuild-determinism fix).</b> Because the credit events are keyed by
/// an economic INTENT id (never an InstanceId) and the drainer folds streams in UNORDERED sequence, a
/// resolution can be folded BEFORE its open. So <see cref="ResolveAsync"/> records the resolution even
/// when no open row exists yet — a RESOLVED tombstone — and a later open no-ops on the conflict; either
/// arrival order converges to the same terminal state, so a full rebuild re-derives the same OUTSTANDING
/// set as the incremental build (ADR-PC-043 slot 3).
/// </para>
/// </remarks>
public interface IIouLedgerStore
{
    /// <summary>
    /// Record an undeliverable credit as an OUTSTANDING IOU, idempotently: a row whose
    /// <c>intent_id</c> already exists is left untouched, so a re-delivered <c>CreditUnapplied</c>
    /// never records the IOU twice, and an open arriving AFTER its resolution's tombstone leaves the
    /// intent RESOLVED (never re-opened) — the commutativity that makes the fold rebuild-deterministic.
    /// </summary>
    Task RecordUnappliedAsync(UndeliverableCreditRow iou, CancellationToken ct = default);

    /// <summary>
    /// Resolve an intent's undeliverable credit, recording the resolution facts and the resolving
    /// event's identity. Commutative: transitions an OUTSTANDING row when the open was already folded,
    /// or records a RESOLVED tombstone when the resolution is folded first — both return
    /// <see cref="CreditResolutionResult.Transitioned"/>. A duplicate resolution of an already-resolved
    /// intent transitions nothing and returns <see cref="CreditResolutionResult.AlreadyResolved"/> so
    /// the caller can surface it (a reconciliation signal, never a double-pay).
    /// </summary>
    Task<CreditResolutionResult> ResolveAsync(
        string originalIntentId, string resolutionIntentId, string reappliedRef, long reappliedAmountCents,
        DateOnly reappliedAt, Guid resolvedStreamId, long resolvedSequence, CancellationToken ct = default);

    /// <summary>
    /// The currently-OUTSTANDING IOUs — the operator's "which credits are still owed, to whom, and how
    /// old" list — oldest first (by <c>unapplied_at</c>, then <c>intent_id</c> for stable ordering).
    /// </summary>
    Task<IReadOnlyList<UndeliverableCreditRow>> GetOutstandingAsync(CancellationToken ct = default);

    /// <summary>Truncate the whole IOU set for a clean rebuild (truncate-then-refold, ADR-PC-043).</summary>
    Task TruncateAsync(CancellationToken ct = default);
}

/// <summary>
/// PostgreSQL-backed <see cref="IIouLedgerStore"/>. Hand-rolled, Npgsql-only, all
/// <c>undeliverable_credits</c> SQL private to this type — the storage-boundary discipline of
/// <see cref="PostgresAccountHoldStore"/> applied to the IOU/escheat read model (migration 0024,
/// ADR-PC-010). The idempotent record is <c>INSERT … ON CONFLICT DO NOTHING</c> on <c>intent_id</c>;
/// the resolution is an <c>UPDATE … WHERE state = 'OUTSTANDING'</c> transition, falling back to a
/// RESOLVED-tombstone <c>INSERT … ON CONFLICT DO NOTHING</c> when the open has not been folded yet — so
/// the fold is commutative and rebuild-deterministic (a duplicate resolution affects zero rows in both).
/// </summary>
public sealed class PostgresIouLedgerStore(string connectionString) : IIouLedgerStore
{
    public async Task RecordUnappliedAsync(UndeliverableCreditRow iou, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(iou);

        // ON CONFLICT DO NOTHING on intent_id: the lifecycle idempotency key (ADR-PC-043) —
        // a re-delivered CreditUnapplied re-inserts the same row as a no-op, never a second IOU.
        const string sql = """
            INSERT INTO undeliverable_credits
                (intent_id, beneficiary_ref, amount_cents, reason, unapplied_at, state,
                 unapplied_stream_id, unapplied_sequence)
            VALUES
                (@intent_id, @beneficiary_ref, @amount_cents, @reason, @unapplied_at, 'OUTSTANDING',
                 @unapplied_stream_id, @unapplied_sequence)
            ON CONFLICT (intent_id) DO NOTHING;
            """;

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("intent_id", iou.IntentId);
        command.Parameters.AddWithValue("beneficiary_ref", iou.BeneficiaryRef);
        command.Parameters.AddWithValue("amount_cents", iou.AmountCents);
        command.Parameters.AddWithValue("reason", iou.Reason);
        command.Parameters.AddWithValue("unapplied_at", iou.UnappliedAt);
        command.Parameters.AddWithValue("unapplied_stream_id", iou.UnappliedStreamId);
        command.Parameters.AddWithValue("unapplied_sequence", iou.UnappliedSequence);
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<CreditResolutionResult> ResolveAsync(
        string originalIntentId, string resolutionIntentId, string reappliedRef, long reappliedAmountCents,
        DateOnly reappliedAt, Guid resolvedStreamId, long resolvedSequence, CancellationToken ct = default)
    {
        // COMMUTATIVE fold (ADR-PC-043 slot 3 — the rebuild-determinism fix): the credit events are
        // keyed by an economic INTENT id, NOT an InstanceId, and the drainer folds streams UNORDERED,
        // so a resolution can be folded BEFORE its open. Two statements make the fold order-independent:
        //   1. UPDATE an OUTSTANDING row -> the normal open-then-resolve transition.
        //   2. If that touches zero rows, INSERT a RESOLVED TOMBSTONE (no open facts) ON CONFLICT DO
        //      NOTHING -> the resolve-before-open case records the resolution now; a later
        //      CreditUnapplied for that intent no-ops on the intent_id conflict rather than re-opening.
        // Either arrival order converges to the same terminal state and the same OUTSTANDING set.
        const string updateSql = """
            UPDATE undeliverable_credits
            SET state = 'RESOLVED',
                resolution_intent_id = @resolution_intent_id,
                reapplied_ref = @reapplied_ref,
                reapplied_amount_cents = @reapplied_amount_cents,
                reapplied_at = @reapplied_at,
                resolved_stream_id = @resolved_stream_id,
                resolved_sequence = @resolved_sequence
            WHERE intent_id = @intent_id AND state = 'OUTSTANDING';
            """;

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using (var command = new NpgsqlCommand(updateSql, connection))
        {
            AddResolutionParameters(
                command, originalIntentId, resolutionIntentId, reappliedRef, reappliedAmountCents,
                reappliedAt, resolvedStreamId, resolvedSequence);
            if (await command.ExecuteNonQueryAsync(ct) == 1)
            {
                return CreditResolutionResult.Transitioned;
            }
        }

        // Zero rows updated: either no row exists yet (resolve-before-open) or the intent is already
        // RESOLVED (a duplicate/late resolution). ONE tombstone INSERT tells them apart: it lands
        // (1 row) only when no row existed — the resolve-before-open case, folded as Transitioned via
        // the tombstone; a conflict (0 rows) means a row already exists, and since the UPDATE did not
        // match it that row is already RESOLVED — a reconciliation signal (AlreadyResolved), never a
        // double-pay (ADR-PC-043).
        const string tombstoneSql = """
            INSERT INTO undeliverable_credits
                (intent_id, state, resolution_intent_id, reapplied_ref, reapplied_amount_cents,
                 reapplied_at, resolved_stream_id, resolved_sequence)
            VALUES
                (@intent_id, 'RESOLVED', @resolution_intent_id, @reapplied_ref, @reapplied_amount_cents,
                 @reapplied_at, @resolved_stream_id, @resolved_sequence)
            ON CONFLICT (intent_id) DO NOTHING;
            """;

        await using var tombstone = new NpgsqlCommand(tombstoneSql, connection);
        AddResolutionParameters(
            tombstone, originalIntentId, resolutionIntentId, reappliedRef, reappliedAmountCents,
            reappliedAt, resolvedStreamId, resolvedSequence);
        return await tombstone.ExecuteNonQueryAsync(ct) == 1
            ? CreditResolutionResult.Transitioned
            : CreditResolutionResult.AlreadyResolved;
    }

    private static void AddResolutionParameters(
        NpgsqlCommand command, string originalIntentId, string resolutionIntentId, string reappliedRef,
        long reappliedAmountCents, DateOnly reappliedAt, Guid resolvedStreamId, long resolvedSequence)
    {
        command.Parameters.AddWithValue("intent_id", originalIntentId);
        command.Parameters.AddWithValue("resolution_intent_id", resolutionIntentId);
        command.Parameters.AddWithValue("reapplied_ref", reappliedRef);
        command.Parameters.AddWithValue("reapplied_amount_cents", reappliedAmountCents);
        command.Parameters.AddWithValue("reapplied_at", reappliedAt);
        command.Parameters.AddWithValue("resolved_stream_id", resolvedStreamId);
        command.Parameters.AddWithValue("resolved_sequence", resolvedSequence);
    }

    public async Task<IReadOnlyList<UndeliverableCreditRow>> GetOutstandingAsync(CancellationToken ct = default)
    {
        // Oldest first: the operator's IOU-aging read scans the OUTSTANDING partial index by
        // unapplied_at (migration 0024), so the oldest owed credits surface first.
        const string sql = """
            SELECT intent_id, beneficiary_ref, amount_cents, reason, unapplied_at, state,
                   unapplied_stream_id, unapplied_sequence, resolution_intent_id, reapplied_ref,
                   reapplied_amount_cents, reapplied_at, resolved_stream_id, resolved_sequence
            FROM undeliverable_credits
            WHERE state = 'OUTSTANDING'
            ORDER BY unapplied_at, intent_id;
            """;

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var command = new NpgsqlCommand(sql, connection);
        return await ReadRowsAsync(command, ct);
    }

    public async Task TruncateAsync(CancellationToken ct = default)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var command = new NpgsqlCommand("TRUNCATE TABLE undeliverable_credits;", connection);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task<IReadOnlyList<UndeliverableCreditRow>> ReadRowsAsync(
        NpgsqlCommand command, CancellationToken ct)
    {
        var rows = new List<UndeliverableCreditRow>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            rows.Add(new UndeliverableCreditRow(
                IntentId: reader.GetString(0),
                BeneficiaryRef: reader.GetString(1),
                AmountCents: reader.GetInt64(2),
                Reason: reader.GetString(3),
                UnappliedAt: reader.GetFieldValue<DateOnly>(4),
                State: reader.GetString(5),
                UnappliedStreamId: reader.GetGuid(6),
                UnappliedSequence: reader.GetInt64(7),
                ResolutionIntentId: reader.IsDBNull(8) ? null : reader.GetString(8),
                ReappliedRef: reader.IsDBNull(9) ? null : reader.GetString(9),
                ReappliedAmountCents: reader.IsDBNull(10) ? null : reader.GetInt64(10),
                ReappliedAt: reader.IsDBNull(11) ? null : reader.GetFieldValue<DateOnly>(11),
                ResolvedStreamId: reader.IsDBNull(12) ? null : reader.GetGuid(12),
                ResolvedSequence: reader.IsDBNull(13) ? null : reader.GetInt64(13)));
        }

        return rows;
    }
}
