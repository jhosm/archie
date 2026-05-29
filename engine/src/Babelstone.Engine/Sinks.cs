using Babelstone.EventStore;

namespace Babelstone.Engine;

/// <summary>
/// The write seam for the engine's single mutation point (A.8). The durable runtime
/// appends THROUGH this rather than calling <see cref="IEventStore"/> directly, so
/// side-effect-freedom for simulation is structural: a runtime composed with
/// <see cref="NullSink"/> physically cannot write the log or outbox — there is no
/// <c>dry_run</c> flag whose one missed branch could leak a real event onto the bus.
/// </summary>
public interface IEventSink
{
    Task AppendAsync(
        Guid streamId,
        long expectedVersion,
        IReadOnlyList<EventEnvelope> events,
        IReadOnlyList<OutboxRow> outboxRows,
        CancellationToken ct = default);
}

/// <summary>The durable sink: appends events + outbox in one transaction via the event store.</summary>
public sealed class EventStoreSink(IEventStore store) : IEventSink
{
    public Task AppendAsync(
        Guid streamId,
        long expectedVersion,
        IReadOnlyList<EventEnvelope> events,
        IReadOnlyList<OutboxRow> outboxRows,
        CancellationToken ct = default)
        => store.AppendAsync(streamId, expectedVersion, events, outboxRows, ct);
}

/// <summary>
/// The simulation sink: discards everything. A runtime wired with this never touches
/// the database — the structural guarantee A.8 (AC#3) requires.
/// </summary>
public sealed class NullSink : IEventSink
{
    public Task AppendAsync(
        Guid streamId,
        long expectedVersion,
        IReadOnlyList<EventEnvelope> events,
        IReadOnlyList<OutboxRow> outboxRows,
        CancellationToken ct = default)
        => Task.CompletedTask;
}
