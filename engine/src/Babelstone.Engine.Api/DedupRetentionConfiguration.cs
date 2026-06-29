using Babelstone.EventStore;

namespace Babelstone.Engine.Api;

/// <summary>
/// Builds the co-hosted retention sweep's <see cref="DedupRetentionOptions"/> from the host's
/// configuration (the <c>Engine:DedupRetention</c> section), falling back to the record's own
/// conservative defaults for any key left unset. Mirrors <see cref="HostPackLoading"/>'s shape: a
/// pure, host-side config-to-options map, extracted from <c>Program.cs</c> so the binding is unit
/// testable without booting the whole host.
/// </summary>
/// <remarks>
/// <para>
/// All four knobs are now config-overridable, not just the two retention windows. The split matters:
/// <see cref="DedupRetentionOptions.CommandDedupRetention"/> and
/// <see cref="DedupRetentionOptions.InboxRetention"/> are correctness-bounded windows (ADR-PC-029 —
/// the command window is a FLOOR, not a knob to shorten casually), whereas
/// <see cref="DedupRetentionOptions.BatchSize"/> and <see cref="DedupRetentionOptions.SweepInterval"/>
/// are pure OPERATIONAL tuning: no value of either can break correctness (every setting only trades
/// drain throughput against lock / WAL pressure), so they are the safest knobs to expose and the most
/// likely to need an in-prod adjust (drain a first backlog faster, or ease off if the deletes pressure
/// the primary) WITHOUT a recompile and redeploy.
/// </para>
/// <para>
/// Any absent key keeps the record's default — so an unconfigured host behaves exactly as before this
/// section existed, and the conservative defaults (3-year command window, 30-day inbox window, 10k
/// batch, 6h cadence) remain the contract.
/// </para>
/// </remarks>
public static class DedupRetentionConfiguration
{
    /// <summary>The configuration section every retention knob is read from.</summary>
    public const string Section = "Engine:DedupRetention";

    /// <summary>
    /// Maps the <see cref="Section"/> config keys onto a <see cref="DedupRetentionOptions"/>, leaving any
    /// unset key at its default. Reads <c>CommandDedup</c> / <c>Inbox</c> / <c>SweepInterval</c> as
    /// <see cref="TimeSpan"/> and <c>BatchSize</c> as <see cref="int"/>.
    /// </summary>
    public static DedupRetentionOptions FromConfiguration(IConfiguration configuration, string connectionString)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        // One defaults instance, reused for every fallback (was newed-up per-field inline in Program.cs).
        var defaults = new DedupRetentionOptions { ConnectionString = connectionString };

        return defaults with
        {
            CommandDedupRetention =
                configuration.GetValue<TimeSpan?>($"{Section}:CommandDedup") ?? defaults.CommandDedupRetention,
            InboxRetention =
                configuration.GetValue<TimeSpan?>($"{Section}:Inbox") ?? defaults.InboxRetention,
            BatchSize =
                configuration.GetValue<int?>($"{Section}:BatchSize") ?? defaults.BatchSize,
            SweepInterval =
                configuration.GetValue<TimeSpan?>($"{Section}:SweepInterval") ?? defaults.SweepInterval,
        };
    }
}
