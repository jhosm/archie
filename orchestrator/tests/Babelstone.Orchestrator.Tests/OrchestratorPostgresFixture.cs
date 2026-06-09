using Babelstone.Orchestrator.Migrations;
using Testcontainers.PostgreSql;
using Xunit;

namespace Babelstone.Orchestrator.Tests;

/// <summary>
/// One PostgreSQL container with the orchestrator's migration set applied, shared across a
/// test class (lifted from the engine's <c>PostgresEventStoreFixture</c>). Tests use fresh
/// process ids, so a shared database needs no per-test reset.
/// </summary>
public sealed class OrchestratorPostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _pg = new PostgreSqlBuilder("postgres:18-alpine")
        .Build();

    public string ConnectionString => _pg.GetConnectionString();

    public async Task InitializeAsync()
    {
        await _pg.StartAsync();
        await new MigrationRunner(ConnectionString).ApplyAsync();
    }

    public async Task DisposeAsync() => await _pg.DisposeAsync();
}
