using Babelstone.Engine.Api;
using Babelstone.EventStore;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Babelstone.Engine.Api.Tests;

/// <summary>
/// The host-side config binding for the dedup-retention sweep (bd babelstone-e6fr.10): all four knobs
/// of <see cref="DedupRetentionOptions"/> read from the <c>Engine:DedupRetention</c> section, with any
/// unset key falling back to the record's conservative default. These pin OUR key names and the
/// default contract (not the framework's binder) — so a renamed key or a silently-changed default
/// trips a test rather than shipping.
/// </summary>
public sealed class DedupRetentionConfigurationTests
{
    private const string Conn = "Host=localhost;Database=babelstone";

    private static IConfiguration Config(params (string Key, string Value)[] entries) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(entries.Select(e =>
                new KeyValuePair<string, string?>(e.Key, e.Value)))
            .Build();

    [Fact]
    public void All_keys_set_maps_every_knob_and_the_connection_string()
    {
        var options = DedupRetentionConfiguration.FromConfiguration(
            Config(
                ("Engine:DedupRetention:CommandDedup", "500.00:00:00"),  // 500 days
                ("Engine:DedupRetention:Inbox", "10.00:00:00"),          // 10 days
                ("Engine:DedupRetention:BatchSize", "2500"),
                ("Engine:DedupRetention:SweepInterval", "02:00:00")),    // 2 hours
            Conn);

        Assert.Equal(TimeSpan.FromDays(500), options.CommandDedupRetention);
        Assert.Equal(TimeSpan.FromDays(10), options.InboxRetention);
        Assert.Equal(2500, options.BatchSize);
        Assert.Equal(TimeSpan.FromHours(2), options.SweepInterval);
        Assert.Equal(Conn, options.ConnectionString);
    }

    [Fact]
    public void No_keys_set_keeps_the_conservative_defaults()
    {
        var options = DedupRetentionConfiguration.FromConfiguration(Config(), Conn);

        // The documented default contract (DedupRetentionOptions): 3-year command window, 30-day inbox
        // window, 10k batch, 6h cadence. An unconfigured host behaves exactly as before the section existed.
        Assert.Equal(TimeSpan.FromDays(1095), options.CommandDedupRetention);
        Assert.Equal(TimeSpan.FromDays(30), options.InboxRetention);
        Assert.Equal(10_000, options.BatchSize);
        Assert.Equal(TimeSpan.FromHours(6), options.SweepInterval);
        Assert.Equal(Conn, options.ConnectionString);
    }

    [Fact]
    public void Only_tuning_knobs_set_binds_them_without_disturbing_the_windows()
    {
        // The new capability in isolation: BatchSize + SweepInterval (the safe operational knobs) bind
        // from config while the correctness-bounded retention windows stay at their defaults.
        var options = DedupRetentionConfiguration.FromConfiguration(
            Config(
                ("Engine:DedupRetention:BatchSize", "500"),
                ("Engine:DedupRetention:SweepInterval", "00:30:00")),  // 30 minutes
            Conn);

        Assert.Equal(500, options.BatchSize);
        Assert.Equal(TimeSpan.FromMinutes(30), options.SweepInterval);
        Assert.Equal(TimeSpan.FromDays(1095), options.CommandDedupRetention);
        Assert.Equal(TimeSpan.FromDays(30), options.InboxRetention);
    }
}
