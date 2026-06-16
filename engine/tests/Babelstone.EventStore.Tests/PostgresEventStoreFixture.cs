using Babelstone.EventStore.Migrations;
using Babelstone.TestFixtures;
using Testcontainers.PostgreSql;
using Xunit;

namespace Babelstone.EventStore.Tests;

/// <summary>
/// One PostgreSQL 18 container with the migration set applied, shared across a test
/// class. Tests use fresh stream ids, so a shared database needs no per-test reset.
/// </summary>
public sealed class PostgresEventStoreFixture : IAsyncLifetime
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
