namespace Babelstone.EventStore;

/// <summary>
/// The storage boundary for the engine's source of truth. Implementations are the
/// ONLY code that touches the <c>events</c> and <c>outbox</c> tables; no other
/// assembly can construct an INSERT against them from a leaked helper.
/// </summary>
public interface IEventStore
{
    /// <summary>
    /// Appends events and their outbox rows in ONE local PostgreSQL transaction
    /// (ADR-PC-001 §P2 / ADR-IC-004 §P6) — the <c>ES_ATOMIC_APPEND_OUTBOX</c> fitness
    /// function. Either both tables commit or neither does.
    /// </summary>
    /// <param name="streamId">The stream being appended to.</param>
    /// <param name="expectedVersion">
    /// The sequence_number the caller believes is the current head of the stream.
    /// <c>-1</c> means "the stream must not yet exist." If the actual head differs,
    /// the append is rejected with <see cref="ConcurrencyException"/> and nothing is
    /// written. The supplied events must carry contiguous sequence numbers starting
    /// at <paramref name="expectedVersion"/> + 1.
    /// </param>
    /// <param name="events">The event rows to append; must be non-empty.</param>
    /// <param name="outboxRows">The outbox rows to write in the same transaction — at most one per event, and
    /// zero when every appended event is uncatalogued (store-only, ADR-IC-017). Never more than the
    /// <paramref name="events"/> count.</param>
    /// <param name="commandId">
    /// The caller's deterministic command id (ADR-PC-029 slot 4). When non-null, the append is
    /// made <b>idempotent</b>: a receipt is written to the <c>command_dedup</c> ledger
    /// (migration 0015) in the SAME transaction as the events + outbox, so a replay of the same
    /// command id finds the receipt and raises <see cref="DuplicateCommandException"/> (carrying
    /// the original head) instead of appending a second time. The receipt INSERT precedes the
    /// events INSERT, so a concurrent duplicate loses on the command id before it can open a
    /// second stream. <c>null</c> (the default) preserves the non-idempotent append unchanged.
    /// </param>
    /// <param name="ct">Cancels the append before the transaction commits.</param>
    /// <exception cref="ConcurrencyException">The stream head did not match <paramref name="expectedVersion"/>.</exception>
    /// <exception cref="DuplicateCommandException">
    /// <paramref name="commandId"/> was already applied — the caller returns the original outcome.
    /// </exception>
    Task AppendAsync(
        Guid                         streamId,
        long                         expectedVersion,
        IReadOnlyList<EventEnvelope> events,
        IReadOnlyList<OutboxRow>     outboxRows,
        Guid?                        commandId = null,
        CancellationToken            ct = default);

    /// <summary>
    /// Streams a stream's events in <c>sequence_number</c> order; the caller folds
    /// them into state. Snapshot-aware callers pass <paramref name="fromSequence"/> =
    /// the snapshot's <c>AtSequence</c> + 1 to read only the tail (the snapshot-then-tail
    /// rehydrate path); a cold replay passes 0 and reads the whole stream.
    /// </summary>
    /// <param name="streamId">The stream to read.</param>
    /// <param name="fromSequence">Inclusive lower bound on <c>sequence_number</c>; 0 reads from the start.</param>
    /// <param name="ct">Cancels the enumeration between events.</param>
    IAsyncEnumerable<EventEnvelope> LoadAsync(
        Guid              streamId,
        long              fromSequence = 0,
        CancellationToken ct = default);

    /// <summary>
    /// Returns the distinct <c>stream_id</c>s for a family — the set the async projection
    /// drainer iterates, folding each stream's tail (via <see cref="LoadAsync"/>) from its
    /// per-stream checkpoint. A read-only companion to the append path; it does NOT touch the
    /// <c>ES_ATOMIC_APPEND_OUTBOX</c> write transaction. The events table carries no cluster-wide
    /// total order, so projection draining is per stream.
    /// </summary>
    Task<IReadOnlyList<Guid>> ReadStreamIdsAsync(string family, CancellationToken ct = default);
}

/// <summary>
/// Thrown when an append's <c>expectedVersion</c> does not match the stream's actual
/// head — either caught early by the explicit version check or by the
/// <c>UNIQUE (stream_id, sequence_number)</c> constraint under a concurrent race.
/// The transaction is rolled back; no events or outbox rows are written.
/// </summary>
public sealed class ConcurrencyException(Guid streamId, long expectedVersion, long actualVersion)
    : Exception($"Concurrency conflict on stream {streamId}: expected head {expectedVersion}, found {actualVersion}.")
{
    public Guid StreamId { get; } = streamId;
    public long ExpectedVersion { get; } = expectedVersion;
    public long ActualVersion { get; } = actualVersion;
}

/// <summary>
/// Thrown when an append carries a <c>commandId</c> that has ALREADY been applied (the
/// <c>command_dedup</c> primary key collided inside the append transaction) — the
/// ENGINE_COMMAND_IDEMPOTENT guarantee (ADR-PC-029 slot 4). Unlike a
/// <see cref="ConcurrencyException"/> (a genuinely conflicting writer), this is a benign
/// replay: the transaction is rolled back (no second append) and the caller returns the
/// <b>original</b> outcome carried here. The exact dedup row first written by the original
/// append is read back to populate <see cref="StreamId"/> + <see cref="CommitSequence"/>.
/// </summary>
public sealed class DuplicateCommandException(Guid commandId, Guid streamId, long commitSequence)
    : Exception($"Command {commandId} was already applied to stream {streamId} at commit_sequence {commitSequence}.")
{
    public Guid CommandId { get; } = commandId;
    public Guid StreamId { get; } = streamId;
    public long CommitSequence { get; } = commitSequence;
}
