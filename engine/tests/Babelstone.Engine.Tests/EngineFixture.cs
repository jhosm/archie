using Babelstone.Engine;
using Babelstone.EventStore;
using Babelstone.EventStore.Migrations;
using Babelstone.TestFixtures;
using Testcontainers.PostgreSql;
using Xunit;

namespace Babelstone.Engine.Tests;

/// <summary>PG18 with migrations applied, plus wired runtimes over the counter family.</summary>
public sealed class EngineFixture : IAsyncLifetime
{
    private static readonly DateTimeOffset Clock = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly PostgreSqlContainer _pg = new PostgreSqlBuilder("postgres:18-alpine")
        .Build();

    public string ConnectionString => _pg.GetConnectionString();
    public HandlerRegistry Handlers { get; } = CounterFamilyModule.Registry();
    public JsonEventSerializer Serializer { get; } = new();

    public PostgresEventStore Store => new(ConnectionString);
    public PostgresSnapshotStore SnapshotStorage => new(ConnectionString);

    public AggregateRuntime<CounterState> DurableRuntime(bool withSnapshots = false)
    {
        SnapshotStore<CounterState>? snapshots = withSnapshots
            ? new SnapshotStore<CounterState>(SnapshotStorage, new JsonStateSerializer<CounterState>())
            : null;
        return new AggregateRuntime<CounterState>(
            Store, new EventStoreSink(Store), Handlers, Serializer, new NullPiiProtector(),
            new FixedTimeProvider(Clock), () => new CounterState(0), snapshots);
    }

    /// <summary>
    /// A runtime wired the way the LIVE host wires it (A.11): a real snapshot store PLUS a per-N
    /// <see cref="ISnapshotPolicy"/>, so the post-commit write side fires. <paramref name="onSnapshotError"/>
    /// lets a test assert the fail-soft sink is invoked rather than the exception propagating.
    /// </summary>
    public AggregateRuntime<CounterState> SnapshottingRuntime(
        long everyNEvents,
        Action<Exception>? onSnapshotError = null,
        ISnapshotStorage? storage = null,
        ICalendarBoundaryPolicy? calendarBoundaryPolicy = null,
        TimeProvider? clock = null)
        => new(
            Store, new EventStoreSink(Store), Handlers, Serializer, new NullPiiProtector(),
            clock ?? new FixedTimeProvider(Clock), () => new CounterState(0),
            new SnapshotStore<CounterState>(storage ?? SnapshotStorage, new JsonStateSerializer<CounterState>()),
            snapshotPolicy: new CountBasedSnapshotPolicy(everyNEvents),
            onSnapshotError: onSnapshotError,
            calendarBoundaryPolicy: calendarBoundaryPolicy);

    public SimulationRuntime<CounterState> Simulation()
        => new(Store, Handlers, Serializer, () => new CounterState(0));

    public AppendContext Context() => new("counter", "pt.2026.1", "counter@2026.1", "test", Clock);

    public async Task InitializeAsync()
    {
        await _pg.GatedStartAsync();
        await new MigrationRunner(ConnectionString).ApplyAsync();
    }

    public async Task DisposeAsync() => await _pg.DisposeAsync();
}
