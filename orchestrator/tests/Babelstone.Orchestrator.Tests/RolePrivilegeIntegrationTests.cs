using Npgsql;
using Xunit;

namespace Babelstone.Orchestrator.Tests;

/// <summary>
/// Asserts the <c>babelstone_orchestrator</c> runtime-role privilege envelope the migration
/// provisions (ADR-PC-001 §P3, lifted convention; the engine's 0002 role test does the same
/// for <c>babelstone_engine</c>). The role is enforced at the DATABASE boundary, not by code
/// review: a buggy PR that DELETEs a saga row or UPDATEs the append-only transition history
/// is rejected by Postgres, not merely caught in review.
/// </summary>
[Trait("Category", "Integration")]
[Collection(nameof(OrchestratorPostgresCollection))]
public sealed class RolePrivilegeIntegrationTests(OrchestratorPostgresFixture fixture)
{
    [Fact]
    public async Task The_runtime_role_can_INSERT_and_UPDATE_saga_state_but_not_DELETE()
    {
        await using var connection = await OpenAsync();
        var processId = Guid.NewGuid();

        await SetRoleAsync(connection);

        // INSERT and UPDATE are the saga aggregate's lifecycle — the one place the
        // orchestrator MUTATES in place (optimistic concurrency, ADR-IC-003 §P1).
        await ExecAsync(connection,
            "INSERT INTO saga_state (process_id, saga_type, state) VALUES (@p, 'ConstitutionProcess', 'STARTED');",
            ("p", processId));
        await ExecAsync(connection,
            "UPDATE saga_state SET state = 'PARALLEL_VALIDATION', version = version + 1 WHERE process_id = @p;",
            ("p", processId));

        // DELETE on saga_state is denied — a saga row is never deleted at runtime.
        var ex = await Assert.ThrowsAsync<PostgresException>(() => ExecAsync(connection,
            "DELETE FROM saga_state WHERE process_id = @p;", ("p", processId)));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, ex.SqlState);
    }

    [Fact]
    public async Task The_runtime_role_cannot_UPDATE_the_append_only_transition_history()
    {
        await using var connection = await OpenAsync();
        var processId = Guid.NewGuid();

        // Seed a saga + transition as the owner (migration role), then SET ROLE and try to mutate.
        await ExecAsync(connection,
            "INSERT INTO saga_state (process_id, saga_type, state) VALUES (@p, 'ConstitutionProcess', 'STARTED');",
            ("p", processId));
        await ExecAsync(connection,
            "INSERT INTO saga_transition (process_id, from_state, to_state, event_type) " +
            "VALUES (@p, 'STARTED', 'PARALLEL_VALIDATION', 'ConstitutionRequested');",
            ("p", processId));

        await SetRoleAsync(connection);

        var ex = await Assert.ThrowsAsync<PostgresException>(() => ExecAsync(connection,
            "UPDATE saga_transition SET to_state = 'COMPLETED' WHERE process_id = @p;", ("p", processId)));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, ex.SqlState);
    }

    private static async Task SetRoleAsync(NpgsqlConnection connection) =>
        await ExecAsync(connection, "SET ROLE babelstone_orchestrator;");

    private static async Task ExecAsync(NpgsqlConnection connection, string sql, params (string Name, object Value)[] args)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in args)
        {
            command.Parameters.AddWithValue(name, value);
        }

        await command.ExecuteNonQueryAsync();
    }

    private async Task<NpgsqlConnection> OpenAsync()
    {
        var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        return connection;
    }
}
