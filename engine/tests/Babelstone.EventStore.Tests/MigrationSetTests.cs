using Babelstone.EventStore.Migrations;
using Xunit;

namespace Babelstone.EventStore.Tests;

/// <summary>
/// Infrastructure-free checks on the embedded migration set. These run in the
/// default engine CI job (no Docker), guarding the forward-only packaging
/// discipline before any DDL reaches a database.
/// </summary>
public sealed class MigrationSetTests
{
    [Fact]
    public void Discovers_the_embedded_migrations()
    {
        Assert.NotEmpty(MigrationSet.All);
        Assert.Contains(MigrationSet.All, m => m.Name == "events_and_outbox");
        Assert.Contains(MigrationSet.All, m => m.Name == "append_only_role");
    }

    [Fact]
    public void Versions_are_strictly_ascending_and_unique()
    {
        var versions = MigrationSet.All.Select(m => m.Version).ToArray();
        var sortedDistinct = versions.Distinct().Order().ToArray();
        Assert.Equal(sortedDistinct, versions);
    }

    [Fact]
    public void Every_migration_carries_sql()
    {
        Assert.All(MigrationSet.All, m => Assert.False(string.IsNullOrWhiteSpace(m.Sql)));
    }

    [Fact]
    public void Events_migration_declares_the_PC001_envelope_columns()
    {
        // The §P1 column contract is the integration boundary; a missing column is
        // a contract regression worth catching without standing up a database.
        var sql = MigrationSet.All.Single(m => m.Name == "events_and_outbox").Sql;
        string[] contractColumns =
        [
            "event_id", "stream_id", "sequence_number", "event_type", "event_schema_version",
            "family", "partition_key", "pack_version", "schema_version", "valid_time",
            "transaction_time", "causation_id", "correlation_id", "actor", "payload",
            "payload_schema_id",
        ];
        Assert.All(contractColumns, c => Assert.Contains(c, sql));
    }
}
