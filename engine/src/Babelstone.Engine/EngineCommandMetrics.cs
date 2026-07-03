using System.Diagnostics.Metrics;
using Babelstone.EventStore;
using Babelstone.Telemetry;

namespace Babelstone.Engine;

/// <summary>
/// The engine command-surface SLIs (bd babelstone-f0ic.15.6): <c>commands_total</c> /
/// <c>events_appended_total</c> (recorded in the <see cref="AggregateRuntime{TState}"/> shell after
/// a commit) and <c>command_dedup_hits_total</c> (recorded where an idempotent replay is detected —
/// the <see cref="MeteredCommandLog"/> pre-check and the in-transaction
/// <see cref="DuplicateCommandException"/> path). Instruments live on the shared
/// <see cref="BabelstoneTelemetry.Meter"/> (ADR-IC-007) so a host turns them on with one
/// <c>AddMeter</c>; with no listener attached <c>Add</c> is a near-zero-cost no-op.
/// </summary>
/// <remarks>
/// Emitted from the IMPURE runtime shell only (ADR-PC-010 / OBS_SPAN_PRODUCT_SEMANTICS): a counter
/// bump observes an append that already committed (or a replay that was refused) — it never runs in
/// a pure decider, a fold, or replayed state, so replay determinism is untouched. The only
/// dimension carried is <see cref="BabelstoneAttributes.AggregateType"/> (the family/topic name) —
/// operational tier, admitted by the metric View allowlist, never PII (ADR-PC-004 /
/// OBS_NO_PII_ATTRS). Metric names are wire contracts (the Metrics lens and future alert rules read
/// them by exact string) — add-and-deprecate, never rename.
/// </remarks>
public static class EngineCommandMetrics
{
    private static readonly Counter<long> Commands =
        BabelstoneTelemetry.Meter.CreateCounter<long>(
            BabelstoneAttributes.CommandsMetric,
            description: "Commands whose decided events committed through the runtime shell (a dedup replay counts on command_dedup_hits_total instead).");

    private static readonly Counter<long> DedupHits =
        BabelstoneTelemetry.Meter.CreateCounter<long>(
            BabelstoneAttributes.CommandDedupHitsMetric,
            description: "Idempotent command replays refused a second apply (ADR-PC-029 slot 4) — the pre-check receipt hit or the in-transaction command_dedup collision.");

    private static readonly Counter<long> EventsAppended =
        BabelstoneTelemetry.Meter.CreateCounter<long>(
            BabelstoneAttributes.EventsAppendedMetric,
            description: "Immutable facts committed to the event log, counted per appended event after the sink transaction succeeds.");

    /// <summary>One command's decided events committed: bump <c>commands_total</c> by one and
    /// <c>events_appended_total</c> by the batch size, tagged by family. Called from the runtime
    /// shell AFTER the sink transaction succeeds — a rolled-back append records nothing.</summary>
    public static void RecordCommandApplied(string family, int eventCount)
    {
        var tag = new KeyValuePair<string, object?>(BabelstoneAttributes.AggregateType, family);
        Commands.Add(1, tag);
        EventsAppended.Add(eventCount, tag);
    }

    /// <summary>An idempotent replay was refused a second apply (ADR-PC-029 slot 4) — the only
    /// visible trace of a dedup hit, since the ledger collision itself is silent.</summary>
    public static void RecordDedupHit() => DedupHits.Add(1);
}

/// <summary>
/// An <see cref="ICommandLog"/> decorator that counts pre-check receipt HITS on
/// <c>command_dedup_hits_total</c> (bd babelstone-f0ic.15.6). Every command endpoint consults the
/// receipt read BEFORE any side effect (ADR-PC-029 slot 4), so decorating the one registration
/// covers every pre-check replay without touching a family endpoint; the concurrent-racer path
/// (the in-transaction PK collision) is counted where <see cref="DuplicateCommandException"/>
/// surfaces in the runtime shell. Pure observation — the receipt (or its absence) passes through
/// unchanged.
/// </summary>
public sealed class MeteredCommandLog(ICommandLog inner) : ICommandLog
{
    public async Task<CommandReceipt?> TryGetAsync(Guid commandId, CancellationToken ct = default)
    {
        var receipt = await inner.TryGetAsync(commandId, ct);
        if (receipt is not null)
        {
            EngineCommandMetrics.RecordDedupHit();
        }

        return receipt;
    }
}
