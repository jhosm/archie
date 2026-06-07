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
    /// <param name="outboxRows">The outbox rows to write in the same transaction; must be non-empty (§P2).</param>
    /// <param name="ct">Cancels the append before the transaction commits.</param>
    /// <exception cref="ConcurrencyException">The stream head did not match <paramref name="expectedVersion"/>.</exception>
    Task AppendAsync(
        Guid                         streamId,
        long                         expectedVersion,
        IReadOnlyList<EventEnvelope> events,
        IReadOnlyList<OutboxRow>     outboxRows,
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
    /// <c>ES_ATOMIC_APPEND_OUTBOX</c> write transaction. (The events table carries no
    /// cluster-wide total order, so projection draining is per stream; a v4 partition-parallel
    /// drain would add a global cursor — out of D.2 scope.)
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
