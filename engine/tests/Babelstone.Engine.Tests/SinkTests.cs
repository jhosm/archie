using Babelstone.Engine;
using Babelstone.EventStore;
using Xunit;

namespace Babelstone.Engine.Tests;

/// <summary>
/// The A.8 structural side-effect-freedom seam, exercised without a database: a durable
/// runtime wired with <see cref="NullSink"/> discards its writes and never reaches the
/// event store. Runs in the default (Docker-free) lane.
/// </summary>
public sealed class SinkTests
{
    [Fact]
    public async Task A_runtime_wired_with_NullSink_writes_nothing_and_never_touches_the_store()
    {
        var runtime = new AggregateRuntime<CounterState>(
            new ThrowingEventStore(),       // any reach into the durable store fails the test
            new NullSink(),
            CounterFamilyModule.Registry(),
            new JsonEventSerializer(),
            new NullPiiProtector(),
            TimeProvider.System,
            () => new CounterState(0));

        // Completes via the discard sink; the store is never called, so no throw.
        await runtime.AppendAsync(
            Guid.NewGuid(), expectedVersion: -1, [new Incremented(5)],
            new AppendContext("counter", "pt.2026.1", "counter@2026.1", "test", DateTimeOffset.UnixEpoch));
    }

    private sealed class ThrowingEventStore : IEventStore
    {
        public Task AppendAsync(
            Guid streamId, long expectedVersion, IReadOnlyList<EventEnvelope> events,
            IReadOnlyList<OutboxRow> outboxRows, CancellationToken ct = default)
            => throw new InvalidOperationException("the durable store must not be reached when wired with NullSink");

        public IAsyncEnumerable<EventEnvelope> LoadAsync(Guid streamId, long fromSequence = 0, CancellationToken ct = default)
            => throw new InvalidOperationException("not used in this test");

        public Task<IReadOnlyList<Guid>> ReadStreamIdsAsync(string family, CancellationToken ct = default)
            => throw new InvalidOperationException("not used in this test");
    }
}
