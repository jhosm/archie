using System.Runtime.CompilerServices;
using System.Text.Json;
using Babelstone.Engine;
using Npgsql;
using Xunit;

namespace Babelstone.EventStore.Tests;

/// <summary>
/// Integration tests for the D.2 projection RUNTIME (<see cref="ProjectionDrainer"/> +
/// <see cref="ProjectionRunner{TState}"/>) over a real PostgreSQL projection store, with a
/// synthetic accumulating fold. Proves the three load-bearing D.2 properties:
/// the drainer materialises the fold (ADR-PC-002 §P4), a re-drain is idempotent (the
/// source_sequence guard — accumulating folds would otherwise double-count), and a cold
/// rebuild reproduces a BIT-IDENTICAL current belief (recorded_at = the event's transaction_time,
/// ADR-PC-010 §P5 determinism).
/// </summary>
[Trait("Category", "Integration")]
public sealed class ProjectionRuntimeIntegrationTests(PostgresEventStoreFixture fixture)
    : IClassFixture<PostgresEventStoreFixture>
{
    private const string Family = "counter";
    private const string Kind = "counter.total";

    private static readonly DateTimeOffset Origin = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private async Task ResetAsync()
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "TRUNCATE projections; TRUNCATE projection_checkpoints;", connection);
        await command.ExecuteNonQueryAsync();
    }

    private (ProjectionDrainer Drainer, IProjectionRunner Runner, InMemoryEventStore Events) BuildRuntime()
    {
        var events = new InMemoryEventStore();
        var checkpoints = new PostgresProjectionCheckpointStore(fixture.ConnectionString);
        var storage = new PostgresProjectionStore(fixture.ConnectionString);
        var store = new ProjectionStore<CounterState>(storage, new JsonStateSerializer<CounterState>());
        var handlers = new HandlerRegistry([
            new HandlerRegistration("counter.Incremented", typeof(CounterIncremented),
                new DispatchableHandler<CounterState, CounterIncremented>(new CounterHandler())),
        ]);
        var runner = new ProjectionRunner<CounterState>(
            Kind, Family, ProjectionMode.Async, handlers, new JsonEventSerializer(), CounterState.Empty, store);
        // The drainer's only clock use is the checkpoint last_processed_at (informational; never
        // asserted) and the rebuild superseded_at on OLD rows (not the current belief) — so the
        // system clock is fine and the rebuild current belief stays event-derived and deterministic.
        var drainer = new ProjectionDrainer(events, checkpoints, TimeProvider.System);
        return (drainer, runner, events);
    }

    [Fact]
    public async Task Drain_materialises_the_accumulated_fold()
    {
        await ResetAsync();
        var (drainer, runner, events) = BuildRuntime();
        var streamId = Guid.NewGuid();
        events.Seed(Envelope(streamId, 0, by: 10));
        events.Seed(Envelope(streamId, 1, by: 5));
        events.Seed(Envelope(streamId, 2, by: 7));

        var folded = await drainer.DrainOnceAsync(runner);

        Assert.Equal(3, folded);
        var current = await ReadCounterAsync(streamId);
        Assert.Equal(22, current.Count);
    }

    [Fact]
    public async Task Redrain_is_idempotent_and_does_not_double_count()
    {
        await ResetAsync();
        var (drainer, runner, events) = BuildRuntime();
        var streamId = Guid.NewGuid();
        events.Seed(Envelope(streamId, 0, by: 10));
        events.Seed(Envelope(streamId, 1, by: 5));

        await drainer.DrainOnceAsync(runner);
        var secondPass = await drainer.DrainOnceAsync(runner); // checkpoint already at head

        Assert.Equal(0, secondPass);
        Assert.Equal(15, (await ReadCounterAsync(streamId)).Count);
    }

    [Fact]
    public async Task Reapplying_an_already_folded_event_is_skipped_by_the_source_sequence_guard()
    {
        await ResetAsync();
        var (_, runner, _) = BuildRuntime();
        var streamId = Guid.NewGuid();

        // Apply seq 0 and 1, then RE-APPLY seq 1 (simulating an at-least-once redelivery after a
        // crash between the projection write and the checkpoint advance). The guard must skip it.
        await runner.ApplyAsync(Envelope(streamId, 0, by: 10));
        await runner.ApplyAsync(Envelope(streamId, 1, by: 5));
        await runner.ApplyAsync(Envelope(streamId, 1, by: 5));

        Assert.Equal(15, (await ReadCounterAsync(streamId)).Count); // not 20
    }

    [Fact]
    public async Task Rebuild_reproduces_a_bit_identical_current_belief()
    {
        await ResetAsync();
        var (drainer, runner, events) = BuildRuntime();
        var streamId = Guid.NewGuid();
        events.Seed(Envelope(streamId, 0, by: 10));
        events.Seed(Envelope(streamId, 1, by: 5));
        events.Seed(Envelope(streamId, 2, by: 7));

        await drainer.DrainOnceAsync(runner);
        var before = await ReadRecordAsync(streamId);

        await drainer.RebuildAsync(runner); // supersede-all + reset checkpoints + re-fold from 0
        var after = await ReadRecordAsync(streamId);

        // recorded_at = event transaction_time and valid_from = event valid_time, so the rebuilt
        // current belief is byte-for-byte identical — the ADR-PC-002 §P4 / ADR-PC-010 §P5 contract.
        Assert.Equal(before.StructuralPayload.ToArray(), after.StructuralPayload.ToArray());
        Assert.Equal(before.RecordedAt, after.RecordedAt);
        Assert.Equal(before.ValidFrom, after.ValidFrom);
        Assert.Equal(before.SourceSequence, after.SourceSequence);
    }

    [Fact]
    public async Task Drain_isolates_streams()
    {
        await ResetAsync();
        var (drainer, runner, events) = BuildRuntime();
        var streamA = Guid.NewGuid();
        var streamB = Guid.NewGuid();
        events.Seed(Envelope(streamA, 0, by: 3));
        events.Seed(Envelope(streamB, 0, by: 8));

        await drainer.DrainOnceAsync(runner);

        Assert.Equal(3, (await ReadCounterAsync(streamA)).Count);
        Assert.Equal(8, (await ReadCounterAsync(streamB)).Count);
    }

    // --- helpers ---

    private async Task<CounterState> ReadCounterAsync(Guid streamId)
    {
        var record = await ReadRecordAsync(streamId);
        return JsonSerializer.Deserialize<CounterState>(record.StructuralPayload.Span)!;
    }

    private async Task<ProjectionRecord> ReadRecordAsync(Guid streamId)
    {
        var record = await new PostgresProjectionStore(fixture.ConnectionString).ReadCurrentBeliefAsync(streamId, Kind);
        Assert.NotNull(record);
        return record;
    }

    private static EventEnvelope Envelope(Guid streamId, long sequence, int by) => new(
        EventId: Guid.NewGuid(),
        StreamId: streamId,
        SequenceNumber: sequence,
        EventType: "counter.Incremented",
        EventSchemaVersion: 1,
        Family: Family,
        PartitionKey: streamId,
        PackVersion: "test",
        SchemaVersion: "counter@1",
        // Deterministic, event-derived stamps — the rebuild reads these back identically.
        ValidTime: Origin.AddDays(sequence),
        TransactionTime: Origin.AddHours(sequence),
        CausationId: null,
        CorrelationId: null,
        Actor: "test",
        Payload: JsonSerializer.SerializeToUtf8Bytes(new CounterIncremented(by)),
        PayloadSchemaId: 0);

    // --- synthetic family ---

    private sealed record CounterState(int Count)
    {
        public static CounterState Empty() => new(0);
    }

    private sealed record CounterIncremented(int By) : DomainEvent;

    private sealed class CounterHandler : IEventHandler<CounterState, CounterIncremented>
    {
        // Accumulating fold — exactly the shape that double-counts if an event is re-applied.
        public HandlerResult<CounterState> Apply(CounterState state, CounterIncremented @event) =>
            HandlerResult<CounterState>.From(state with { Count = state.Count + @event.By });
    }

    private sealed class JsonEventSerializer : IEventSerializer
    {
        public EncodedPayload Encode(DomainEvent @event) =>
            new(JsonSerializer.SerializeToUtf8Bytes(@event, @event.GetType()), 0);

        public DomainEvent Decode(ReadOnlyMemory<byte> payload, Type payloadType) =>
            (DomainEvent)JsonSerializer.Deserialize(payload.Span, payloadType)!;
    }

    private sealed class InMemoryEventStore : IEventStore
    {
        private readonly List<EventEnvelope> _events = [];

        public void Seed(EventEnvelope envelope) => _events.Add(envelope);

        public Task AppendAsync(
            Guid streamId, long expectedVersion, IReadOnlyList<EventEnvelope> events,
            IReadOnlyList<OutboxRow> outboxRows, CancellationToken ct = default) =>
            throw new NotSupportedException("The drainer only reads.");

        public async IAsyncEnumerable<EventEnvelope> LoadAsync(
            Guid streamId, long fromSequence = 0, [EnumeratorCancellation] CancellationToken ct = default)
        {
            foreach (var e in _events
                         .Where(e => e.StreamId == streamId && e.SequenceNumber >= fromSequence)
                         .OrderBy(e => e.SequenceNumber))
            {
                yield return e;
            }

            await Task.CompletedTask;
        }

        public Task<IReadOnlyList<Guid>> ReadStreamIdsAsync(string family, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<Guid>>(
                _events.Where(e => e.Family == family).Select(e => e.StreamId).Distinct().ToList());
    }
}
