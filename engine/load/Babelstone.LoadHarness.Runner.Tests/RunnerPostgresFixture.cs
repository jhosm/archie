using Babelstone.EventStore.Migrations;
using Babelstone.TestFixtures;
using Testcontainers.PostgreSql;
using Xunit;

namespace Babelstone.LoadHarness.Runner.Tests;

/// <summary>
/// One PostgreSQL 18 container with the engine event-store migration set applied, shared across a test
/// class. This is the live event store the <c>EngineProjectionRig</c> / <c>LoadRunner</c> drive their
/// append / projection / cold-replay path against — exactly the topology <c>make load-test</c> runs the
/// host on, so the §G2 measured path is exercised by the Testcontainers integration lane (bd
/// babelstone-2e6q.7) rather than only by a hand-run live stack.
/// </summary>
/// <remarks>
/// Mirrors <c>Babelstone.EventStore.Tests.PostgresEventStoreFixture</c> — that fixture is internal to its
/// own test assembly, so the small dev-container setup is repeated here, reusing the shared
/// <see cref="ContainerStartupGate"/> (via <c>GatedStartAsync</c>) so this lane participates in the same
/// startup throttle as every other Testcontainers class. Each test uses fresh, run-nonce-namespaced
/// stream ids, so a shared database needs no per-test reset.
/// </remarks>
public sealed class RunnerPostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _pg = new PostgreSqlBuilder("postgres:18-alpine")
        .Build();

    public string ConnectionString => _pg.GetConnectionString();

    public async Task InitializeAsync()
    {
        await _pg.GatedStartAsync();
        await new MigrationRunner(ConnectionString).ApplyAsync();
    }

    public async Task DisposeAsync() => await _pg.DisposeAsync();
}
