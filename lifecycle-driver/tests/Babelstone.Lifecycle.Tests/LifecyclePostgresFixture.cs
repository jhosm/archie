using Babelstone.Lifecycle.Migrations;
using Babelstone.TestFixtures;
using Testcontainers.PostgreSql;
using Xunit;

namespace Babelstone.Lifecycle.Tests;

/// <summary>
/// One PostgreSQL container with the lifecycle driver's own migration set applied (the
/// <c>lifecycle_dispatch_ledger</c> series, ADR-PC-038 §Decision 1) — the same fixture shape as the
/// orchestrator's <c>OrchestratorPostgresFixture</c> and the engine's <c>PostgresEventStoreFixture</c>,
/// shared across the integration test collection. Tests use fresh instance ids, so a shared database
/// needs no per-test reset.
/// </summary>
public sealed class LifecyclePostgresFixture : IAsyncLifetime
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

[CollectionDefinition(nameof(LifecyclePostgresCollection))]
public sealed class LifecyclePostgresCollection : ICollectionFixture<LifecyclePostgresFixture>;
