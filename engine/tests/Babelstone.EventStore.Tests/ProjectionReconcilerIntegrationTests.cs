using System.Runtime.CompilerServices;
using System.Text.Json;
using Babelstone.Engine;
using Npgsql;
using Xunit;

namespace Babelstone.EventStore.Tests;

/// <summary>
/// D.5 reconciliation tests for the three event-store §7.1 patterns over a real PostgreSQL
/// projection store (Testcontainers), driven through <see cref="ProjectionReconciler{TState}"/>
/// with a synthetic accumulating fold:
/// <list type="bullet">
/// <item>(a) per-instance state checksum — engine cold-fold hash vs projection belief hash;</item>
/// <item>(b) event-count reconciliation — expected N vs last-processed sequence, gap vs skip;</item>
/// <item>(c) the §7.2 full-rebuild drill — supersede-all + checkpoint reset + cold re-fold,
///     assert byte-identical terminal state.</item>
/// </list>
/// The reconciler is FAMILY-AGNOSTIC: it reuses the same dispatch the runner uses, so the
/// engine-side checksum is the SAME fold the projection materialises, computed independently.
/// </summary>
[Trait("Category", "Integration")]
public sealed class ProjectionReconcilerIntegrationTests(PostgresEventStoreFixture fixture)
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

    [Fact]
    public async Task Checksum_matches_when_the_projection_agrees_with_a_cold_fold()
    {
        await ResetAsync();
        var (drainer, runner, events, reconciler) = Build();
        var streamId = Guid.NewGuid();
        events.Seed(Envelope(streamId, 0, by: 10));
        events.Seed(Envelope(streamId, 1, by: 5));
        events.Seed(Envelope(streamId, 2, by: 7));
        await drainer.DrainOnceAsync(runner);

        var result = await reconciler.ChecksumAsync(streamId, Kind);

        Assert.True(result.ProjectionExists);
        Assert.True(result.Match);
        Assert.Equal(result.EngineHash, result.ProjectionHash);
    }

    [Fact]
    public async Task Checksum_mismatches_when_the_projection_has_drifted()
    {
        await ResetAsync();
        var (drainer, runner, events, reconciler) = Build();
        var streamId = Guid.NewGuid();
        events.Seed(Envelope(streamId, 0, by: 10));
        events.Seed(Envelope(streamId, 1, by: 5));
        await drainer.DrainOnceAsync(runner);

        // Corrupt the materialised belief in place (a stand-in for accumulated handler drift): the
        // current belief now says 999, the cold fold from the log says 15. The checksum must catch it.
        await CorruptCurrentBeliefAsync(streamId, new CounterState(999));

        var result = await reconciler.ChecksumAsync(streamId, Kind);

        Assert.True(result.ProjectionExists);
        Assert.False(result.Match);
        Assert.NotEqual(result.EngineHash, result.ProjectionHash);
    }

    [Fact]
    public async Task Checksum_reports_absent_when_the_projection_was_never_folded()
    {
        await ResetAsync();
        var (_, _, events, reconciler) = Build();
        var streamId = Guid.NewGuid();
        events.Seed(Envelope(streamId, 0, by: 3)); // events exist, but the projection never drained.

        var result = await reconciler.ChecksumAsync(streamId, Kind);

        Assert.False(result.ProjectionExists);
        Assert.False(result.Match);
        Assert.Null(result.ProjectionHash);
    }

    [Fact]
    public async Task EventCount_is_in_sync_when_the_consumer_reached_the_head()
    {
        await ResetAsync();
        var (drainer, runner, events, reconciler) = Build();
        var streamId = Guid.NewGuid();
        events.Seed(Envelope(streamId, 0, by: 1));
        events.Seed(Envelope(streamId, 1, by: 1));
        events.Seed(Envelope(streamId, 2, by: 1));
        var folded = await drainer.DrainOnceAsync(runner);

        var result = await reconciler.EventCountAsync(streamId, Kind, consumerFoldedCount: folded);

        Assert.Equal(3, result.ExpectedCount);
        Assert.Equal(2, result.LastProcessedSequence);
        Assert.Equal(EventCountStatus.InSync, result.Status);
    }

    [Fact]
    public async Task EventCount_reports_a_gap_when_the_consumer_lags_the_head()
    {
        await ResetAsync();
        var (_, runner, events, reconciler) = Build();
        var streamId = Guid.NewGuid();
        events.Seed(Envelope(streamId, 0, by: 1));
        events.Seed(Envelope(streamId, 1, by: 1));
        events.Seed(Envelope(streamId, 2, by: 1));

        // Fold only the first two events (a lagging async consumer): belief at sequence 1, 2 folded.
        await runner.ApplyAsync(Envelope(streamId, 0, by: 1));
        await runner.ApplyAsync(Envelope(streamId, 1, by: 1));

        var result = await reconciler.EventCountAsync(streamId, Kind, consumerFoldedCount: 2);

        Assert.Equal(3, result.ExpectedCount);          // three events exist
        Assert.Equal(1, result.LastProcessedSequence);  // consumer is behind, but in order
        Assert.Equal(2, result.HandledAtOrBelow);       // it folded exactly what it should have
        Assert.Equal(EventCountStatus.Gap, result.Status);
    }

    [Fact]
    public async Task EventCount_reports_a_skip_when_the_belief_advanced_past_unfolded_events()
    {
        await ResetAsync();
        var (_, _, events, reconciler) = Build();
        var streamId = Guid.NewGuid();
        events.Seed(Envelope(streamId, 0, by: 1));
        events.Seed(Envelope(streamId, 1, by: 1));
        events.Seed(Envelope(streamId, 2, by: 1));

        // The belief claims to be at sequence 2 (source_sequence = 2) — three handled events exist
        // at/below it — but the consumer reports it only folded 1. It jumped ahead: a SKIP.
        await WriteBeliefAtSequenceAsync(streamId, new CounterState(1), sourceSequence: 2);

        var result = await reconciler.EventCountAsync(streamId, Kind, consumerFoldedCount: 1);

        Assert.Equal(3, result.ExpectedCount);
        Assert.Equal(2, result.LastProcessedSequence);
        Assert.Equal(3, result.HandledAtOrBelow);        // three handled events exist at/below seq 2
        Assert.Equal(1, result.FoldedCount);             // but only one was folded
        Assert.Equal(EventCountStatus.Skip, result.Status);
    }

    // --- G.5: per-consumer reconciliation contract (event-store §7.3) ---

    [Fact]
    public async Task Contract_drives_the_declared_patterns_and_reports_clean_when_the_consumer_agrees()
    {
        await ResetAsync();
        var (drainer, runner, events, reconciler) = Build();
        var streamId = Guid.NewGuid();
        events.Seed(Envelope(streamId, 0, by: 10));
        events.Seed(Envelope(streamId, 1, by: 5));
        events.Seed(Envelope(streamId, 2, by: 7));
        var folded = await drainer.DrainOnceAsync(runner);

        // The engine's own projection runtime: a contract over all three §7.1 patterns.
        var contract = new ReconciliationContract(
            Consumer: "engine",
            ProjectionKind: Kind,
            Patterns: ReconciliationPatterns.All,
            ContractRef: "contracts/catalog/reconciliation/engine-projection-runtime.reconciliation.yaml");

        var report = await reconciler.ReconcileAsync(contract, streamId, consumerFoldedCount: folded);

        Assert.Equal("engine", report.Contract.Consumer);
        Assert.NotNull(report.Checksum);   // the contract opted into checksum -> it ran
        Assert.True(report.Checksum!.Match);
        Assert.NotNull(report.EventCount); // and into event-count -> it ran
        Assert.Equal(EventCountStatus.InSync, report.EventCount!.Status);
        Assert.True(report.IsClean);
    }

    [Fact]
    public async Task Contract_runs_only_the_patterns_it_declares()
    {
        await ResetAsync();
        var (drainer, runner, events, reconciler) = Build();
        var streamId = Guid.NewGuid();
        events.Seed(Envelope(streamId, 0, by: 3));
        await drainer.DrainOnceAsync(runner);

        // A lighter consumer contract that publishes only the daily checksum — it opts OUT of
        // event-count, so that pattern must not run (its slot stays null, not a failure).
        var contract = new ReconciliationContract(
            Consumer: "analytics",
            ProjectionKind: Kind,
            Patterns: ReconciliationPatterns.Checksum,
            ContractRef: "contracts/catalog/reconciliation/analytics.reconciliation.yaml");

        var report = await reconciler.ReconcileAsync(contract, streamId);

        Assert.NotNull(report.Checksum);
        Assert.Null(report.EventCount);  // opted out -> did not run
        Assert.True(report.IsClean);     // an opted-out pattern never fails the verdict
    }

    [Fact]
    public async Task Contract_reports_unclean_when_the_consumer_has_drifted()
    {
        await ResetAsync();
        var (drainer, runner, events, reconciler) = Build();
        var streamId = Guid.NewGuid();
        events.Seed(Envelope(streamId, 0, by: 10));
        events.Seed(Envelope(streamId, 1, by: 5));
        await drainer.DrainOnceAsync(runner);
        await CorruptCurrentBeliefAsync(streamId, new CounterState(999));

        var contract = new ReconciliationContract(
            Consumer: "acl",
            ProjectionKind: Kind,
            Patterns: ReconciliationPatterns.Checksum | ReconciliationPatterns.EventCount,
            ContractRef: "contracts/catalog/reconciliation/acl.reconciliation.yaml");

        var report = await reconciler.ReconcileAsync(contract, streamId, consumerFoldedCount: 2);

        Assert.False(report.Checksum!.Match);
        Assert.False(report.IsClean); // a drifted checksum on a pattern the contract runs => unclean
    }

    [Theory]
    [InlineData("", Kind, ReconciliationPatterns.All, "ref")]                 // no consumer
    [InlineData("acl", "no_dot_kind", ReconciliationPatterns.All, "ref")]     // not family-prefixed
    [InlineData("acl", Kind, ReconciliationPatterns.None, "ref")]             // reconciles nothing
    [InlineData("acl", Kind, ReconciliationPatterns.All, "")]                 // no catalogued ref
    public async Task Contract_validation_rejects_a_malformed_contract(
        string consumer, string projectionKind, ReconciliationPatterns patterns, string contractRef)
    {
        await ResetAsync();
        var (_, _, _, reconciler) = Build();
        var contract = new ReconciliationContract(consumer, projectionKind, patterns, contractRef);

        await Assert.ThrowsAsync<ArgumentException>(
            () => reconciler.ReconcileAsync(contract, Guid.NewGuid()));
    }

    [Fact]
    public async Task FullRebuildDrill_reports_identical_after_a_cold_rebuild()
    {
        await ResetAsync();
        var (drainer, runner, events, reconciler) = Build();
        var streamId = Guid.NewGuid();
        events.Seed(Envelope(streamId, 0, by: 10));
        events.Seed(Envelope(streamId, 1, by: 5));
        events.Seed(Envelope(streamId, 2, by: 7));
        await drainer.DrainOnceAsync(runner);

        var drill = await reconciler.FullRebuildDrillAsync(drainer, runner, streamId);

        // supersede-all + checkpoint reset + cold re-fold from 0 reproduces the running belief
        // byte-for-byte (recorded_at = event transaction_time, ADR-PC-002 §P4 / ADR-PC-010 §P5).
        Assert.True(drill.Identical);
        Assert.Equal(3, drill.EventsRefolded);
        Assert.NotNull(drill.BeforeHash);
        Assert.Equal(drill.BeforeHash, drill.AfterHash);
    }

    [Fact]
    public async Task FullRebuildDrill_repairs_a_drifted_belief()
    {
        await ResetAsync();
        var (drainer, runner, events, reconciler) = Build();
        var streamId = Guid.NewGuid();
        events.Seed(Envelope(streamId, 0, by: 4));
        events.Seed(Envelope(streamId, 1, by: 6));
        await drainer.DrainOnceAsync(runner);

        // A drifted belief before the drill. The §7.2 rebuild discards beliefs (supersede-all) and
        // re-folds from the log, so AFTER is the correct cold-fold — proving the drill is the repair
        // path, not just a detector. The reconstructed hash equals a fresh checksum's engine hash.
        await CorruptCurrentBeliefAsync(streamId, new CounterState(123456));

        var drill = await reconciler.FullRebuildDrillAsync(drainer, runner, streamId);

        Assert.False(drill.Identical); // the corrupted before-hash differs from the rebuilt after-hash
        var checksum = await reconciler.ChecksumAsync(streamId, Kind);
        Assert.True(checksum.Match);                       // post-rebuild the belief agrees with the log
        Assert.Equal(checksum.EngineHash, drill.AfterHash); // and the rebuilt hash is the cold-fold hash
    }

    // --- composition ---

    private (ProjectionDrainer Drainer, IProjectionRunner Runner, InMemoryEventStore Events, ProjectionReconciler<CounterState> Reconciler)
        Build()
    {
        var events = new InMemoryEventStore();
        var checkpoints = new PostgresProjectionCheckpointStore(fixture.ConnectionString);
        var storage = new PostgresProjectionStore(fixture.ConnectionString);
        var stateSerializer = new JsonStateSerializer<CounterState>();
        var store = new ProjectionStore<CounterState>(storage, stateSerializer);
        var handlers = new HandlerRegistry([
            new HandlerRegistration("counter.Incremented", typeof(CounterIncremented),
                new DispatchableHandler<CounterState, CounterIncremented>(new CounterHandler())),
        ]);
        var eventSerializer = new JsonEventSerializer();
        var runner = new ProjectionRunner<CounterState>(
            Kind, Family, ProjectionMode.Async, handlers, eventSerializer, CounterState.Empty, store);
        var drainer = new ProjectionDrainer(events, checkpoints, TimeProvider.System);
        var reconciler = new ProjectionReconciler<CounterState>(
            events, storage, handlers, eventSerializer, stateSerializer, CounterState.Empty);
        return (drainer, runner, events, reconciler);
    }

    // --- helpers that simulate drift / skips directly on the store ---

    private async Task CorruptCurrentBeliefAsync(Guid streamId, CounterState wrong)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "UPDATE projections SET structural_payload = @p WHERE stream_id = @s AND projection_kind = @k AND superseded_at IS NULL;",
            connection);
        command.Parameters.AddWithValue("p", JsonSerializer.SerializeToUtf8Bytes(wrong));
        command.Parameters.AddWithValue("s", streamId);
        command.Parameters.AddWithValue("k", Kind);
        await command.ExecuteNonQueryAsync();
    }

    private async Task WriteBeliefAtSequenceAsync(Guid streamId, CounterState state, long sourceSequence)
    {
        var record = new ProjectionRecord(
            StreamId: streamId,
            ProjectionKind: Kind,
            SourceSequence: sourceSequence,
            ValidFrom: Origin,
            ValidTo: null,
            RecordedAt: Origin,
            SupersededAt: null,
            StructuralPayload: JsonSerializer.SerializeToUtf8Bytes(state),
            PiiCiphertext: ReadOnlyMemory<byte>.Empty);
        await new PostgresProjectionStore(fixture.ConnectionString).WriteAsync(record);
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
            IReadOnlyList<OutboxRow> outboxRows, Guid? commandId = null, CancellationToken ct = default) =>
            throw new NotSupportedException("The reconciler and drainer only read.");

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
