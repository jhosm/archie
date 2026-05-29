using Babelstone.Engine;
using Npgsql;
using Xunit;

namespace Babelstone.Engine.Tests;

/// <summary>
/// A.8: a simulation projects forward over committed history WITHOUT side effects.
/// The structural guarantee is checked by asserting zero new rows in events, outbox,
/// and snapshots after a simulate call.
/// </summary>
[Trait("Category", "Integration")]
public sealed class SimulationRuntimeIntegrationTests(EngineFixture fixture) : IClassFixture<EngineFixture>
{
    [Fact]
    public async Task Simulation_projects_hypothetical_events_without_writing_anything()
    {
        var streamId = Guid.NewGuid();
        // Seed durable history: total = 8.
        await fixture.DurableRuntime().AppendAsync(streamId, -1, [new Incremented(3), new Incremented(5)], fixture.Context());

        var before = await RowCountsAsync(streamId);

        // Counterfactual: "what if two more increments arrived?" — never persisted.
        var projected = await fixture.Simulation().ProjectAsync(streamId, [new Incremented(10), new Incremented(1)]);

        var after = await RowCountsAsync(streamId);

        Assert.Equal(19, projected.Total);            // 8 + 10 + 1, computed from real history + hypotheticals
        Assert.Equal(before, after);                   // nothing written: events, outbox, snapshots all unchanged
        Assert.Equal((2L, 2L, 0L), after);             // 2 events, 2 outbox rows (from the seed), 0 snapshots
    }

    [Fact]
    public async Task Simulation_does_not_mutate_the_durable_projection()
    {
        var streamId = Guid.NewGuid();
        await fixture.DurableRuntime().AppendAsync(streamId, -1, [new Incremented(4)], fixture.Context());

        await fixture.Simulation().ProjectAsync(streamId, [new Incremented(100)]);

        // The durable state is still just the committed event.
        var hydrated = await fixture.DurableRuntime().LoadAsync(streamId);
        Assert.Equal(4, hydrated.State.Total);
    }

    private async Task<(long Events, long Outbox, long Snapshots)> RowCountsAsync(Guid streamId)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        var events = await ScalarAsync(connection, "SELECT count(*) FROM events WHERE stream_id = @id", streamId);
        var outbox = await ScalarAsync(connection, "SELECT count(*) FROM outbox WHERE aggregate_id = @id", streamId);
        var snapshots = await ScalarAsync(connection, "SELECT count(*) FROM snapshots WHERE stream_id = @id", streamId);
        return (events, outbox, snapshots);
    }

    private static async Task<long> ScalarAsync(NpgsqlConnection connection, string sql, Guid streamId)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("id", streamId);
        return (long)(await command.ExecuteScalarAsync())!;
    }
}
