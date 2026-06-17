using Npgsql;
using Xunit;

namespace Babelstone.EventStore.Tests;

/// <summary>
/// The dedup-ledger retention sweep (bd babelstone-e6fr.10): it deletes the AGED tail of both
/// dedup ledgers — <c>command_dedup</c> (migration 0015) and <c>inbox</c> (migration 0012) — while
/// leaving every row still INSIDE its retention window untouched. The load-bearing assertion is the
/// last one: a command receipt younger than the window is NEVER pruned, so a late at-least-once retry
/// of that command still replays the original outcome (ADR-PC-029 §4) and never opens a duplicate.
/// Tagged Integration so the Docker-free engine CI job skips it; the integration lane runs it.
/// </summary>
[Trait("Category", "Integration")]
public sealed class DedupRetentionSweeperIntegrationTests(PostgresEventStoreFixture fixture)
    : IClassFixture<PostgresEventStoreFixture>
{
    [Fact]
    public async Task Sweep_deletes_aged_rows_and_keeps_rows_inside_the_window()
    {
        // Windows: command receipts kept 30 days, inbox rows kept 7 days. Seed two rows in each
        // table — one comfortably OUTSIDE the window (should be deleted) and one INSIDE it (must stay).
        var options = new DedupRetentionOptions
        {
            ConnectionString = fixture.ConnectionString,
            CommandDedupRetention = TimeSpan.FromDays(30),
            InboxRetention = TimeSpan.FromDays(7),
        };

        var agedCommand = Guid.NewGuid();   // 90 days old → deleted
        var freshCommand = Guid.NewGuid();  // 1 day old → kept (a late retry must still replay it)
        await InsertCommandDedupAsync(agedCommand, ageDays: 90);
        await InsertCommandDedupAsync(freshCommand, ageDays: 1);

        var agedInbox = Guid.NewGuid();     // 30 days old → deleted
        var freshInbox = Guid.NewGuid();    // 1 day old → kept
        await InsertInboxAsync(agedInbox, ageDays: 30);
        await InsertInboxAsync(freshInbox, ageDays: 1);

        var result = await new DedupRetentionSweeper(options).SweepOnceAsync();

        // Each table had exactly one aged row this run; the sweep deleted exactly those.
        Assert.Equal(1, result.CommandDedupDeleted);
        Assert.Equal(1, result.InboxDeleted);

        // The aged rows are gone; the within-window rows survive — the correctness guarantee.
        Assert.False(await CommandDedupExistsAsync(agedCommand));
        Assert.True(await CommandDedupExistsAsync(freshCommand));
        Assert.False(await InboxExistsAsync(agedInbox));
        Assert.True(await InboxExistsAsync(freshInbox));
    }

    [Fact]
    public async Task Sweep_caps_each_cycle_at_the_batch_size()
    {
        // A small batch cap over a larger aged backlog: one cycle deletes exactly the cap, the next
        // drains the rest — so a first sweep over a huge backlog stays a bounded range-delete.
        var options = new DedupRetentionOptions
        {
            ConnectionString = fixture.ConnectionString,
            CommandDedupRetention = TimeSpan.FromDays(30),
            InboxRetention = TimeSpan.FromDays(7),
            BatchSize = 3,
        };

        var aged = new List<Guid>();
        for (var i = 0; i < 5; i++)
        {
            var id = Guid.NewGuid();
            aged.Add(id);
            await InsertCommandDedupAsync(id, ageDays: 90);
        }

        var sweeper = new DedupRetentionSweeper(options);

        var first = await sweeper.SweepOnceAsync();
        Assert.Equal(3, first.CommandDedupDeleted); // capped at the batch size

        var second = await sweeper.SweepOnceAsync();
        Assert.Equal(2, second.CommandDedupDeleted); // the remaining aged rows

        foreach (var id in aged)
        {
            Assert.False(await CommandDedupExistsAsync(id));
        }
    }

    // Insert directly with a back-dated created_at/processed_at so the row reads as N days old against
    // the DB clock the sweep compares to (now() - retention). The age is set in the DB (now() - interval)
    // so it is single-clock with the sweep's cutoff, never host-clock-derived.

    private async Task InsertCommandDedupAsync(Guid commandId, int ageDays)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO command_dedup (command_id, stream_id, commit_sequence, created_at)
            VALUES (@command_id, @stream_id, 0, now() - make_interval(days => @age_days));
            """,
            connection);
        command.Parameters.AddWithValue("command_id", commandId);
        command.Parameters.AddWithValue("stream_id", Guid.NewGuid());
        command.Parameters.AddWithValue("age_days", ageDays);
        await command.ExecuteNonQueryAsync();
    }

    private async Task InsertInboxAsync(Guid messageId, int ageDays)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO inbox (message_id, source_topic, processed_at)
            VALUES (@message_id, 'term_deposit', now() - make_interval(days => @age_days));
            """,
            connection);
        command.Parameters.AddWithValue("message_id", messageId);
        command.Parameters.AddWithValue("age_days", ageDays);
        await command.ExecuteNonQueryAsync();
    }

    private Task<bool> CommandDedupExistsAsync(Guid commandId) =>
        ExistsAsync("SELECT count(*) FROM command_dedup WHERE command_id = @id", commandId);

    private Task<bool> InboxExistsAsync(Guid messageId) =>
        ExistsAsync("SELECT count(*) FROM inbox WHERE message_id = @id", messageId);

    private async Task<bool> ExistsAsync(string sql, Guid id)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("id", id);
        return (long)(await command.ExecuteScalarAsync())! > 0;
    }
}
