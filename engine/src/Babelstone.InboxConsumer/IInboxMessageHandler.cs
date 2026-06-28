using Npgsql;

namespace Babelstone.InboxConsumer;

/// <summary>
/// The THIN dispatch seam between the inbox consume loop and whatever processes a deduplicated
/// message. This is the plug-point the saga state machine (in PG) wires a real handler
/// into — this assembly deliberately does NOT build a saga (orchestrator/ stays a stub).
/// </summary>
/// <remarks>
/// <para>
/// The handler runs INSIDE the same transaction as the inbox dedup INSERT (Document 04: the
/// <c>message_id</c> insert and the business effect commit together, so a message is processed
/// effectively-once). It is given the open <see cref="NpgsqlConnection"/> and
/// <see cref="NpgsqlTransaction"/> so any DB effect (a saga-state row, another local-outbox row —
/// the outbox→inbox→outbox chain of Document 04) lands atomically with the dedup row: either both
/// commit or both roll back. The consume loop owns the transaction lifecycle (begin/commit/rollback)
/// and the offset commit — the handler just contributes its effect.
/// </para>
/// <para>
/// <b>Determinism / purity (ADR-PC-010):</b> the message's domain fold path stays pure — no
/// clock, no randomness, no out-of-transaction I/O. A handler that must persist state does it
/// through the supplied connection/transaction; a handler that must call an external system follows
/// Document 04's rule (call a retryable-idempotent endpoint BEFORE the inbox insert, or emit a
/// local-outbox event rather than blocking the transaction on a network round-trip). The default
/// dev handler (<see cref="NullInboxMessageHandler"/>) is a pure no-op.
/// </para>
/// </remarks>
public interface IInboxMessageHandler
{
    /// <summary>
    /// Handle one deduplicated message. Called at most once per <c>message_id</c> per consumer (the
    /// inbox PK guarantees it): a redelivery is filtered out by the dedup INSERT before the loop ever
    /// reaches this. Throwing rolls the whole transaction back (dedup row included), so the offset is
    /// NOT committed and the message is redelivered — make the effect safe to retry.
    /// </summary>
    /// <param name="message">The decoded message (dedup id + domain event + structural envelope).</param>
    /// <param name="connection">The open consumer-DB connection the dedup row was inserted on.</param>
    /// <param name="transaction">The open transaction the dedup INSERT and this effect share.</param>
    /// <param name="ct">Cancellation for a graceful shutdown.</param>
    /// <returns>
    /// An optional short, operational-tier <c>result_summary</c> for the inbox row (Document 04's
    /// optional column) — a saga-step label or null. NEVER a NIF/IBAN/name/amount or any PII.
    /// </returns>
    Task<string?> HandleAsync(
        InboxMessage message,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken ct = default);
}

/// <summary>
/// The default dispatch target until the saga is plugged in: a pure no-op that records nothing
/// beyond the inbox dedup row itself (it returns no <c>result_summary</c>). It proves the seam —
/// dedup, transaction, and offset commit all work — without asserting any saga behaviour, the same
/// way a dev-host stub stands in for the real ACL.
/// </summary>
public sealed class NullInboxMessageHandler : IInboxMessageHandler
{
    public Task<string?> HandleAsync(
        InboxMessage message,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken ct = default)
        => Task.FromResult<string?>(null);
}
