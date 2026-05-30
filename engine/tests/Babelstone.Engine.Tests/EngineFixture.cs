using Babelstone.Engine;
using Babelstone.EventStore;
using Babelstone.EventStore.Migrations;
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

    public SimulationRuntime<CounterState> Simulation()
        => new(Store, Handlers, Serializer, () => new CounterState(0));

    public AppendContext Context() => new("counter", "pt.2026.1", "counter@2026.1", "test", Clock);

    public async Task InitializeAsync()
    {
        await _pg.StartAsync();
        await new MigrationRunner(ConnectionString).ApplyAsync();
    }

    public async Task DisposeAsync() => await _pg.DisposeAsync();
}
