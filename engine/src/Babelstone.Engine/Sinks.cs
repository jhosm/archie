using Babelstone.EventStore;

namespace Babelstone.Engine;

/// <summary>
/// The write seam for the engine's single mutation point (A.8). The durable runtime
/// appends THROUGH this rather than calling <see cref="IEventStore"/> directly. The
/// structural side-effect-freedom guarantee lives in <see cref="SimulationRuntime{TState}"/>,
/// which takes NO <see cref="IEventSink"/> at all — so it physically cannot write the log
/// or outbox; there is no <c>dry_run</c> flag whose one missed branch could leak a real
/// event onto the bus. <see cref="NullSink"/> is the discard sink for tests and dry-run
/// wiring that still want the durable runtime's shape without its writes.
/// </summary>
public interface IEventSink
{
    Task AppendAsync(
        Guid streamId,
        long expectedVersion,
        IReadOnlyList<EventEnvelope> events,
        IReadOnlyList<OutboxRow> outboxRows,
        Guid? commandId = null,
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
        Guid? commandId = null,
        CancellationToken ct = default)
        // commandId threads through to the event store's append transaction, where a non-null
        // value makes the append idempotent on the command id (ADR-PC-029 slot 4).
        => store.AppendAsync(streamId, expectedVersion, events, outboxRows, commandId, ct);
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
        Guid? commandId = null,
        CancellationToken ct = default)
        => Task.CompletedTask;
}
