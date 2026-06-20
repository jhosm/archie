using Xunit;

namespace Babelstone.LoadHarness.Runner.Tests;

/// <summary>
/// Docker-free tests for the L.3e synchronous_commit connection-string composition (bd babelstone-2e6q.5).
/// The §P1 sync-replication toggle is pure connection-string composition — the Npgsql <c>Options</c>
/// keyword carries <c>-c synchronous_commit=&lt;value&gt;</c> so every session the store opens commits
/// under it — and needs NO engine-core change and NO live database to verify the composition is correct.
/// </summary>
public sealed class ReplicationLatencyCompositionTests
{
    [Fact]
    public void Synchronous_commit_is_composed_into_the_connection_string_options()
    {
        var composed = EngineProjectionRig.WithSynchronousCommit("Host=h;Database=d;Username=u;Password=p", "on");
        Assert.Contains("synchronous_commit=on", composed);
    }

    [Fact]
    public void An_existing_options_value_is_preserved_not_clobbered()
    {
        var withExisting = EngineProjectionRig.WithSynchronousCommit(
            "Host=h;Database=d;Username=u;Password=p;Options=-c statement_timeout=5000", "local");

        Assert.Contains("statement_timeout=5000", withExisting);
        Assert.Contains("synchronous_commit=local", withExisting);
    }

    [Theory]
    [InlineData("on")]
    [InlineData("local")]
    [InlineData("off")]
    public void Each_synchronous_commit_value_round_trips(string value)
    {
        var composed = EngineProjectionRig.WithSynchronousCommit("Host=h;Database=d;Username=u;Password=p", value);
        Assert.Contains($"synchronous_commit={value}", composed);
    }
}
