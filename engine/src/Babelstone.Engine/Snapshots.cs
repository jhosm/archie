using Babelstone.EventStore;

namespace Babelstone.Engine;

/// <summary>A typed snapshot of projection state (feature-design event-store §8).</summary>
/// <param name="CreatedAt">When the snapshot row was physically written (a wall-clock DB stamp).</param>
/// <param name="TransactionTime">
/// The event-derived transaction_time of the head event the snapshot covers (ADR-PC-010 §P5) — the
/// append-stamped instant, distinct from <paramref name="CreatedAt"/> (when the row was written). Lets
/// rehydrate seed last_updated from the snapshot for a stream fully covered with no tail (ADR-PC-003
/// §P3). Null for a pre-0017 snapshot row, which then falls back to the prior null-on-no-tail behaviour.
/// </param>
public sealed record Snapshot<TState>(
    long AtSequence, Guid LastEventId, string StateHash, TState State, bool Trusted,
    DateTimeOffset CreatedAt, DateTimeOffset? TransactionTime = null);

/// <summary>Serializes projection state to/from bytes for snapshot persistence. Tests supply one (e.g. JSON).</summary>
public interface IStateSerializer<TState>
{
    byte[] Serialize(TState state);
    TState Deserialize(ReadOnlyMemory<byte> bytes);
}

/// <summary>
/// The typed layer over A.4's byte-oriented <see cref="ISnapshotStorage"/>: serializes
/// <typeparamref name="TState"/>, computes the §8.3 hash (state ‖ last_event_id), and
/// verifies it on read so a tampered or mis-sequenced snapshot is rejected, not trusted.
/// </summary>
public sealed class SnapshotStore<TState>(ISnapshotStorage storage, IStateSerializer<TState> serializer)
{
    public async Task<Snapshot<TState>?> TryGetAsync(Guid streamId, CancellationToken ct = default)
        => Verify(streamId, await storage.TryGetLatestAsync(streamId, ct));

    /// <summary>
    /// The typed wrapper over <see cref="ISnapshotStorage.TryGetAtOrBeforeAsync"/>: the latest *valid*
    /// snapshot at or below <paramref name="atOrBeforeSequence"/> (ADR-PC-003 §P1 readLatestSnapshot),
    /// hash-verified the same way as <see cref="TryGetAsync"/>. Returns null when no snapshot covers
    /// the point — the as-of read then folds cold from zero (the §P3 correctness fallback). A snapshot
    /// taken PAST the point is excluded by the storage bound, so the as-of fold can never seed from a
    /// future snapshot.
    /// </summary>
    public async Task<Snapshot<TState>?> TryGetAtOrBeforeAsync(
        Guid streamId, long atOrBeforeSequence, CancellationToken ct = default)
        => Verify(streamId, await storage.TryGetAtOrBeforeAsync(streamId, atOrBeforeSequence, ct));

    private Snapshot<TState>? Verify(Guid streamId, SnapshotRecord? record)
    {
        if (record is null)
        {
            return null;
        }

        // Verify the stored hash before trusting the snapshot (§8.3): the worst
        // event-sourcing failure mode is a silently-wrong snapshot read as truth.
        var expected = SnapshotHash.Compute(record.State.Span, record.LastEventId);
        if (!string.Equals(expected, record.StateHash, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Snapshot for stream {streamId} at sequence {record.AtSequence} failed hash verification.");
        }

        return new Snapshot<TState>(
            record.AtSequence,
            record.LastEventId,
            record.StateHash,
            serializer.Deserialize(record.State),
            record.Trusted,
            record.CreatedAt,
            record.TransactionTime);
    }

    /// <summary>
    /// Writes a snapshot at <paramref name="atSequence"/>. <paramref name="transactionTime"/> is the
    /// event-derived transaction_time of the head event the snapshot covers (the append's stamp,
    /// ADR-PC-010 §P5) — carried so rehydrate can seed last_updated for a fully-covered stream. Defaults
    /// to <paramref name="createdAt"/> so a caller that has only one timestamp (e.g. test wiring that
    /// passes a fixed instant) keeps the pre-0017 single-stamp behaviour; the runtime passes the real
    /// append transaction_time explicitly.
    /// </summary>
    public Task PutAsync(
        Guid streamId, long atSequence, Guid lastEventId, TState state, DateTimeOffset createdAt,
        DateTimeOffset? transactionTime = null, CancellationToken ct = default)
    {
        var bytes = serializer.Serialize(state);
        var hash = SnapshotHash.Compute(bytes, lastEventId);
        // Trusted defaults false — advisory until six months of passing drills (§8.3).
        var record = new SnapshotRecord(
            streamId, atSequence, lastEventId, hash, bytes, Trusted: false, createdAt,
            TransactionTime: transactionTime ?? createdAt);
        return storage.PutAsync(record, ct);
    }
}

/// <summary>Inputs a snapshot policy weighs (feature-design event-store §8.1).</summary>
/// <param name="EventsSinceSnapshot">Count of events folded since the last snapshot (drives the per-N trigger).</param>
/// <param name="IsLifecycleBoundary">The just-folded event is a lifecycle boundary (constitution, maturity, …).</param>
/// <param name="IsCalendarBoundary">A reporting-period boundary (month/year end) was crossed.</param>
public sealed record SnapshotContext(long EventsSinceSnapshot, bool IsLifecycleBoundary, bool IsCalendarBoundary);

/// <summary>The §8.1 trigger decision. Triggers compose: a snapshot is taken if any fires.</summary>
public interface ISnapshotPolicy
{
    bool ShouldSnapshot(SnapshotContext ctx);
}

/// <summary>
/// Default policy: the per-N trigger (§8.1), configurable threshold. Lifecycle and
/// calendar boundaries also fire when the caller flags them — those flags are supplied
/// by the family (which knows its lifecycle events) and the runtime (which owns the
/// transaction-time clock), so the engine stays family-agnostic. This is the COMPOSING
/// policy of ADR-PC-003 §P2: a snapshot is taken if ANY of the three triggers fires.
/// </summary>
public sealed class CountBasedSnapshotPolicy(long threshold = 100) : ISnapshotPolicy
{
    public bool ShouldSnapshot(SnapshotContext ctx)
        => ctx.EventsSinceSnapshot >= threshold || ctx.IsLifecycleBoundary || ctx.IsCalendarBoundary;
}

/// <summary>The calendar granularity a snapshot aligns to (ADR-PC-003 §P2 / event-store §8.1).</summary>
public enum CalendarGranularity
{
    /// <summary>No calendar trigger — the per-N + lifecycle triggers stand alone.</summary>
    None,

    /// <summary>Month-end alignment: an append in a later month than the previous one is a boundary.</summary>
    Month,

    /// <summary>Year-end alignment: an append in a later year than the previous one is a boundary.</summary>
    Year,
}

/// <summary>
/// Decides whether an append crossed a CALENDAR BOUNDARY (ADR-PC-003 §P2 / event-store §8.1:
/// "month-end and year-end alignment with reporting periods, regardless of event count"), so an as-of
/// query at a period boundary returns without a long replay. The runtime owns the transaction-time
/// clock (ADR-PC-010 §P5), so it asks this policy — never a handler — comparing the PREVIOUS head's
/// transaction_time against THIS append's transaction_time. The comparison is over the
/// already-stamped, event-derived transaction_time (not a fresh wall-clock read), so the boundary is a
/// deterministic function of the log: a replay sees the same boundaries the live append did.
/// </summary>
public interface ICalendarBoundaryPolicy
{
    /// <summary>
    /// Whether this policy can EVER fire — false for a <see cref="CalendarGranularity.None"/> policy. The
    /// runtime checks this to skip the previous-head transaction_time lookup entirely when the calendar
    /// trigger is off, so the common per-N-only wiring pays no extra read.
    /// </summary>
    bool IsActive { get; }

    /// <summary>
    /// True when <paramref name="appendTime"/> falls in a later calendar period (per the configured
    /// granularity) than <paramref name="previousTime"/>. <paramref name="previousTime"/> is null for
    /// the FIRST append on a stream (no prior event to compare against) — the constitution event is
    /// already a lifecycle boundary, so a first append needs no calendar trigger and this returns false.
    /// </summary>
    bool CrossedBoundary(DateTimeOffset? previousTime, DateTimeOffset appendTime);
}

/// <summary>
/// The calendar policy keyed by a single <see cref="CalendarGranularity"/> (per-family/host config:
/// Engine:SnapshotCalendarGranularity). UTC-period comparison — an append whose transaction_time lands
/// in a strictly later month (or year) than the previous head's transaction_time is a boundary. v1
/// default is <see cref="CalendarGranularity.Month"/>; <see cref="CalendarGranularity.None"/> turns the
/// calendar trigger off entirely (the per-N + lifecycle triggers still stand).
/// </summary>
public sealed class CalendarBoundaryPolicy(CalendarGranularity granularity = CalendarGranularity.Month)
    : ICalendarBoundaryPolicy
{
    public bool IsActive => granularity != CalendarGranularity.None;

    public bool CrossedBoundary(DateTimeOffset? previousTime, DateTimeOffset appendTime)
    {
        // No prior event ⇒ nothing to cross (the first append is a lifecycle boundary anyway); a
        // None granularity disables the calendar trigger.
        if (previousTime is not { } previous || granularity == CalendarGranularity.None)
        {
            return false;
        }

        // Compare on a UTC instant so a transaction_time's offset never shifts which period it lands in
        // (transaction_time is stamped UTC by the runtime, but normalise defensively).
        var previousUtc = previous.UtcDateTime;
        var appendUtc = appendTime.UtcDateTime;

        return granularity switch
        {
            // A strictly later month (year first, then month within a year) is a month-end crossing.
            CalendarGranularity.Month =>
                appendUtc.Year > previousUtc.Year
                || (appendUtc.Year == previousUtc.Year && appendUtc.Month > previousUtc.Month),
            CalendarGranularity.Year => appendUtc.Year > previousUtc.Year,
            _ => false,
        };
    }
}
