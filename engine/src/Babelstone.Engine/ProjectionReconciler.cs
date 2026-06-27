using System.Diagnostics.Metrics;
using System.Security.Cryptography;
using Babelstone.EventStore;
using Babelstone.Telemetry;

namespace Babelstone.Engine;

/// <summary>
/// The outcome of a per-instance state-checksum reconciliation (event-store §7.1 pattern (a)):
/// the engine's independently-folded state hash vs the projection's current-belief hash.
/// <see cref="Match"/> is true iff the two SHA-256 digests are byte-identical. A mismatch is
/// consumer drift since the last reconciliation — the §7.1 "daily checksum" finding.
/// </summary>
/// <param name="EngineHash">SHA-256 over the engine's cold-folded state (from the event log alone).</param>
/// <param name="ProjectionHash">SHA-256 over the projection's current-belief structural payload.</param>
/// <param name="ProjectionExists">
/// False when no current belief exists for the pair (the projection has not folded this stream
/// yet). A reconciliation against an absent projection never <see cref="Match"/>es a non-empty stream.
/// </param>
public sealed record ChecksumReconciliation(string EngineHash, string? ProjectionHash, bool ProjectionExists)
{
    /// <summary>The §7.1 verdict: the engine fold and the projection agree byte-for-byte.</summary>
    public bool Match => ProjectionExists && string.Equals(EngineHash, ProjectionHash, StringComparison.Ordinal);
}

/// <summary>Whether an event-count reconciliation found the consumer behind, in sync, or having skipped events.</summary>
public enum EventCountStatus
{
    /// <summary>Last-processed sequence equals the stream head: the consumer is fully caught up.</summary>
    InSync,

    /// <summary>
    /// Last-processed sequence is BELOW the head but every event up to it was consumed in order:
    /// the consumer is merely lagging. Acceptable for an async projection (event-store §7.1) — the
    /// gap closes on the next drain.
    /// </summary>
    Gap,

    /// <summary>
    /// Events were SKIPPED — the count of events the consumer actually folded is fewer than the
    /// number at or below its last-processed sequence, so it advanced past events it never applied.
    /// This is the §7.1 "alert" case (lost/dropped events), distinct from a benign lag.
    /// </summary>
    Skip,
}

/// <summary>
/// The outcome of an event-count reconciliation (event-store §7.1 pattern (b)): the engine
/// publishes its monotonic per-instance event count; the consumer reports the sequence it has
/// processed and how many events it actually folded. A <see cref="EventCountStatus.Gap"/> is acceptable lag; a
/// <see cref="EventCountStatus.Skip"/> means events were lost and is alertable.
/// </summary>
/// <param name="ExpectedCount">Events the engine has on this stream (head sequence + 1).</param>
/// <param name="LastProcessedSequence">The consumer's last-folded <c>sequence_number</c> (−1 = none).</param>
/// <param name="HandledAtOrBelow">
/// How many events the projection genuinely HANDLES at or below <see cref="LastProcessedSequence"/>
/// — the count it SHOULD have folded. Event types this projection ignores are excluded, so an
/// accrual-only projection that legitimately skips maturity events is not mistaken for a skip.
/// </param>
/// <param name="FoldedCount">
/// How many events the consumer actually applied — the consumer-REPORTED count (the engine's own
/// projection reports it from its drain). If it is below <see cref="HandledAtOrBelow"/>, the
/// consumer advanced its sequence past events it never folded: the §7.1 alertable skip.
/// </param>
public sealed record EventCountReconciliation(
    long ExpectedCount, long LastProcessedSequence, long HandledAtOrBelow, long FoldedCount)
{
    public EventCountStatus Status =>
        // A skip: the belief reflects fewer handled events than truly exist at/below the sequence the
        // consumer claims to have processed — it jumped ahead. Distinct from a benign gap (merely
        // behind the head). Note ExpectedCount counts ALL events; the head can be a type the
        // projection ignores, so InSync keys off the consumer reaching the head sequence, not a
        // handled-count equality.
        FoldedCount < HandledAtOrBelow ? EventCountStatus.Skip
        : LastProcessedSequence + 1 < ExpectedCount ? EventCountStatus.Gap
        : EventCountStatus.InSync;
}

/// <summary>
/// The outcome of a §7.2 full-rebuild drill (event-store §7.1 pattern (c)): the projection's
/// current-belief hash before the rebuild vs after a supersede-all + checkpoint-reset + cold
/// re-fold-from-0. <see cref="Identical"/> is true iff the terminal state is byte-identical — the
/// invariant the drill exists to prove (and the slow-drift bug class it catches when it fails).
/// </summary>
/// <param name="BeforeHash">SHA-256 over the running projection's current belief before the rebuild.</param>
/// <param name="AfterHash">SHA-256 over the rebuilt projection's current belief.</param>
/// <param name="EventsRefolded">Events the rebuild drain re-folded across the family's streams.</param>
public sealed record RebuildReconciliation(string? BeforeHash, string? AfterHash, int EventsRefolded)
{
    /// <summary>The §7.2 verdict: the cold rebuild reproduced the running state exactly.</summary>
    public bool Identical => string.Equals(BeforeHash, AfterHash, StringComparison.Ordinal);
}

/// <summary>
/// Which of the three event-store §7.1 reconciliation patterns a consumer's contract opts into.
/// A <see cref="ReconciliationContract"/> declares its subset: the engine's own projection runtime
/// runs all three; a lighter analytics consumer might publish only the daily <see cref="Checksum"/>
/// and report its <see cref="EventCount"/>. The flags mirror the §7.1 table verbatim.
/// </summary>
[Flags]
public enum ReconciliationPatterns
{
    /// <summary>The consumer participates in no reconciliation pattern (degenerate; flagged at construction).</summary>
    None = 0,

    /// <summary>Pattern (a): the daily per-instance state checksum (event-store §7.1).</summary>
    Checksum = 1 << 0,

    /// <summary>Pattern (b): the continuous event-count reconciliation (event-store §7.1).</summary>
    EventCount = 1 << 1,

    /// <summary>Pattern (c): the periodic §7.2 full-rebuild drill (event-store §7.1/§7.2).</summary>
    FullRebuild = 1 << 2,

    /// <summary>All three §7.1 patterns — the engine's own projection runtime's contract.</summary>
    All = Checksum | EventCount | FullRebuild,
}

/// <summary>
/// A per-consumer reconciliation contract (event-store §7.3): a downstream consumer's declared,
/// catalogued statement of what it reconciles against the engine's emitted events — which projection
/// <see cref="ProjectionKind"/> it derives, which §7.1 <see cref="Patterns"/> it participates in, and
/// the catalogued <see cref="ContractRef"/> that documents how its rebuilds are coordinated. The
/// reconciler is generic over this contract rather than over an ad-hoc (streamId, kind) pair, so the
/// SAME engine can drive the engine's own projection runtime, the GL/ACL consumer, the notification
/// consumer, and any analytics/BI consumer from their declared contracts (§7.3 "every downstream
/// system … is a consumer subject to reconciliation").
/// </summary>
/// <remarks>
/// <para>
/// NO PII, by construction (ADR-PC-004 §P2 / the no-PII-on-the-durable-bus rule): a contract carries
/// only structural <em>references</em> — the consumer's stable name, its projection-kind discriminator,
/// and a relative path to the catalogued descriptor. A depositor name, NIF, or IBAN never appears in a
/// contract, the same guarantee the AsyncAPI catalogue and the Avro payloads give.
/// </para>
/// <para>
/// <see cref="ContractRef"/> is the bridge to the catalogue's governance (event-store §7.3 →
/// integration_concepts §08 / ADR-IC-015): it points at the consumer's descriptor under
/// <c>contracts/catalog/reconciliation/</c>. The descriptor is the human/portal-readable side; this
/// record is the executable side the reconciler drives.
/// </para>
/// </remarks>
/// <param name="Consumer">
/// The consumer's stable name — the same identity used in the AsyncAPI <c>x-authorized-consumers</c>
/// list (e.g. <c>engine</c>, <c>acl</c>, <c>notification</c>). A reference, never PII.
/// </param>
/// <param name="ProjectionKind">
/// The family-prefixed projection discriminator the consumer reconciles, e.g.
/// <c>term_deposit.deposit_position</c> (the same <see cref="IProjectionRunner.Kind"/> the runner uses).
/// </param>
/// <param name="Patterns">Which §7.1 patterns this consumer's contract opts into.</param>
/// <param name="ContractRef">
/// Relative path to the catalogued reconciliation descriptor (under <c>contracts/catalog/reconciliation/</c>)
/// that documents this contract for the portal and auditors. A reference, never PII.
/// </param>
public sealed record ReconciliationContract(
    string Consumer,
    string ProjectionKind,
    ReconciliationPatterns Patterns,
    string ContractRef)
{
    /// <summary>
    /// Validates the contract is non-degenerate and PII-free in shape: a named consumer, a
    /// family-prefixed kind, at least one §7.1 pattern, and a catalogued reference. Throws on a
    /// malformed contract so a mis-declared consumer fails fast rather than silently reconciling nothing.
    /// </summary>
    public ReconciliationContract EnsureValid()
    {
        if (string.IsNullOrWhiteSpace(Consumer))
        {
            throw new ArgumentException("A reconciliation contract must name its consumer.", nameof(Consumer));
        }

        if (string.IsNullOrWhiteSpace(ProjectionKind) || !ProjectionKind.Contains('.'))
        {
            throw new ArgumentException(
                $"ProjectionKind '{ProjectionKind}' must be a family-prefixed discriminator (e.g. term_deposit.deposit_position).",
                nameof(ProjectionKind));
        }

        if (Patterns == ReconciliationPatterns.None)
        {
            throw new ArgumentException(
                $"Consumer '{Consumer}' declares no §7.1 reconciliation pattern — a contract that reconciles nothing is a misconfiguration.",
                nameof(Patterns));
        }

        if (string.IsNullOrWhiteSpace(ContractRef))
        {
            throw new ArgumentException(
                $"Consumer '{Consumer}' has no catalogued ContractRef (event-store §7.3 governance).", nameof(ContractRef));
        }

        return this;
    }
}

/// <summary>
/// The outcome of driving one <see cref="ReconciliationContract"/> against one stream (event-store
/// §7.3). Each §7.1 pattern the contract opted into has its result populated; a pattern the contract
/// did NOT declare stays <see langword="null"/>, so the report distinguishes "ran and matched" from
/// "the consumer's contract does not run this pattern". <see cref="IsClean"/> is the per-consumer
/// verdict the reconciliation alerting layer (M.5) keys off.
/// </summary>
/// <param name="Contract">The contract that was driven (carries the consumer name + ref; no PII).</param>
/// <param name="StreamId">The instance reconciled.</param>
/// <param name="Checksum">Pattern (a) result, or <see langword="null"/> if the contract opted out.</param>
/// <param name="EventCount">Pattern (b) result, or <see langword="null"/> if the contract opted out.</param>
public sealed record ConsumerReconciliationReport(
    ReconciliationContract Contract,
    Guid StreamId,
    ChecksumReconciliation? Checksum,
    EventCountReconciliation? EventCount)
{
    /// <summary>
    /// The per-consumer §7.3 verdict: every pattern the contract ran is clean. A <see langword="null"/>
    /// (opted-out) pattern does not fail the verdict — only a pattern that ran and disagreed does. The
    /// §7.2 full-rebuild drill is coordinated separately (it supersedes beliefs) and is not folded in here.
    /// </summary>
    public bool IsClean =>
        (Checksum is null || Checksum.Match) &&
        (EventCount is null || EventCount.Status != EventCountStatus.Skip);
}

/// <summary>
/// The three event-store §7.1 reconciliation patterns — daily per-instance checksum (a),
/// event-count reconciliation (b), and the §7.2 periodic full-rebuild drill (c) — over the
/// hand-rolled Path-A projection substrate (ADR-PC-002). It is the operational layer that makes
/// "the event log is the source of truth" provable: a projection that drifts from a fresh fold of
/// the log is caught here, before regulators or auditors see it (event-store §7).
/// </summary>
/// <remarks>
/// <para>
/// Generic over <typeparamref name="TState"/> and FAMILY-AGNOSTIC by construction — it names no
/// family; the host closes the state type and supplies the same <see cref="HandlerRegistry"/> the
/// family's projection runner uses. So the engine-side checksum (pattern (a)) is the SAME pure
/// fold the projection materialises, computed independently from the event log: the two can only
/// disagree if the materialised belief actually drifted. The spine stays under
/// ENGINE_FAMILY_AGNOSTIC (ADR-PC-021 §P2).
/// </para>
/// <para>
/// The fold reuses <see cref="IDispatchableHandler.ApplyBoxed"/> and skips event types the
/// projection does not handle (mirroring <see cref="ProjectionRunner{TState}"/>), and the hash is
/// SHA-256 over the SAME structural serialization the store persists (<see cref="JsonStateSerializer{TState}"/>
/// is deterministic in declaration order), so a clean reconciliation is genuine byte-identity, not
/// a coincidental digest collision. No clock, no randomness — the reconciler is itself replayable
/// (ADR-PC-010 §P5).
/// </para>
/// </remarks>
public sealed class ProjectionReconciler<TState>(
    IEventStore eventStore,
    IProjectionStorage projectionStorage,
    HandlerRegistry handlers,
    IEventSerializer eventSerializer,
    IStateSerializer<TState> stateSerializer,
    Func<TState> seed)
    where TState : class
{
    /// <summary>
    /// Pattern (a): per-instance state checksum (event-store §7.1, the "daily checksum"). Cold-folds
    /// the stream from the event log alone, hashes that state, and compares it to a hash of the
    /// projection's current belief. A mismatch is consumer drift; an absent projection over a
    /// non-empty stream never matches.
    /// </summary>
    public async Task<ChecksumReconciliation> ChecksumAsync(
        Guid streamId, string projectionKind, CancellationToken ct = default)
    {
        var engineState = await FoldFromLogAsync(streamId, ct);
        var engineHash = HashState(engineState);

        var belief = await projectionStorage.ReadCurrentBeliefAsync(streamId, projectionKind, ct);
        if (belief is null)
        {
            // No projection row yet — Match is false. (A genuinely empty stream folds to the seed
            // state, which the projection also never writes, so an absent belief is the correct read.)
            return new ChecksumReconciliation(engineHash, ProjectionHash: null, ProjectionExists: false);
        }

        var projectionHash = HashBytes(belief.StructuralPayload.Span);
        return new ChecksumReconciliation(engineHash, projectionHash, ProjectionExists: true);
    }

    /// <summary>
    /// Pattern (b): event-count reconciliation (event-store §7.1). The engine's expected count is the
    /// stream head + 1; the consumer's progress is its current belief's <c>source_sequence</c>. The
    /// consumer reports how many events it actually folded (<paramref name="consumerFoldedCount"/>) —
    /// for the engine's own projection this is the drain's running tally. The reconciler reads the log
    /// to compute how many events the projection SHOULD have folded at or below its claimed sequence
    /// (event types it ignores excluded), so a short fold is a SKIP (events lost) and merely trailing
    /// the head is a GAP (acceptable async lag).
    /// </summary>
    public async Task<EventCountReconciliation> EventCountAsync(
        Guid streamId, string projectionKind, long consumerFoldedCount, CancellationToken ct = default)
    {
        var belief = await projectionStorage.ReadCurrentBeliefAsync(streamId, projectionKind, ct);
        var lastProcessed = belief?.SourceSequence ?? -1;

        long expectedCount = 0;
        long handledAtOrBelow = 0;
        await foreach (var envelope in eventStore.LoadAsync(streamId, fromSequence: 0, ct))
        {
            expectedCount = envelope.SequenceNumber + 1;
            if (envelope.SequenceNumber <= lastProcessed && handlers.TryResolveByEventType(envelope.EventType, out _))
            {
                handledAtOrBelow++;
            }
        }

        return new EventCountReconciliation(expectedCount, lastProcessed, handledAtOrBelow, consumerFoldedCount);
    }

    /// <summary>
    /// Pattern (c): the §7.2 full-rebuild drill. Captures the running projection's current-belief
    /// hash, drives <see cref="ProjectionDrainer.RebuildAsync"/> (supersede-all + checkpoint reset +
    /// cold re-fold from sequence 0), then re-reads and compares. <see cref="RebuildReconciliation.Identical"/>
    /// proves the rebuild reproduced the running state; a divergence is the slow-drift bug the drill
    /// exists to surface. Assumes the relay is quiescent for the kind (the drill is a non-production op,
    /// per <see cref="ProjectionDrainer.RebuildAsync"/>'s contract).
    /// </summary>
    public async Task<RebuildReconciliation> FullRebuildDrillAsync(
        ProjectionDrainer drainer, IProjectionRunner runner, Guid streamId, CancellationToken ct = default)
    {
        var before = await projectionStorage.ReadCurrentBeliefAsync(streamId, runner.Kind, ct);
        var beforeHash = before is null ? null : HashBytes(before.StructuralPayload.Span);

        var refolded = await drainer.RebuildAsync(runner, ct);

        var after = await projectionStorage.ReadCurrentBeliefAsync(streamId, runner.Kind, ct);
        var afterHash = after is null ? null : HashBytes(after.StructuralPayload.Span);

        var result = new RebuildReconciliation(beforeHash, afterHash, refolded);

        // Observation boundary (M.5): the §7.2 drill verdict drives two metrics on the shared meter.
        // A divergence is the slow-drift bug class the drill exists to surface (the in-process companion
        // to the ProjectionRebuildDrillStale freshness alert: freshness catches a drill that did not
        // RUN, divergence a drill that RAN and FAILED). A clean drill records its success timestamp on
        // the freshness gauge — evidence the source-of-truth invariant holds for this kind. The metric
        // is a side-effect at this impure boundary, not inside the cold re-fold (ADR-PC-010 §P5).
        if (!result.Identical)
        {
            ReconciliationMetrics.RecordRebuildDivergence(runner.Kind);
        }
        else
        {
            ReconciliationMetrics.RecordDrillSuccess(runner.Kind);
        }

        return result;
    }

    /// <summary>
    /// Drives one per-consumer <see cref="ReconciliationContract"/> against one stream (event-store
    /// §7.3): runs exactly the §7.1 patterns the contract opted into and folds the results into a
    /// single <see cref="ConsumerReconciliationReport"/>. This is the generalised entry point — the
    /// reconciler is no longer called per ad-hoc (streamId, kind) pair but per <em>declared consumer
    /// contract</em>, so the same engine reconciles the engine's own projection runtime, the GL/ACL
    /// consumer, the notification consumer, and any analytics/BI consumer from their catalogued contracts.
    /// </summary>
    /// <remarks>
    /// The §7.2 full-rebuild drill is deliberately NOT run here even when the contract declares
    /// <see cref="ReconciliationPatterns.FullRebuild"/>: a rebuild supersedes live beliefs and is a
    /// coordinated, non-production operation (<see cref="FullRebuildDrillAsync"/>'s contract), so it is
    /// driven separately on the drill calendar. The flag on the contract records that the consumer
    /// <em>participates</em> in the drill; this method does the two cheap, continuous patterns.
    /// </remarks>
    /// <param name="contract">The consumer's declared contract (validated before use).</param>
    /// <param name="streamId">The instance to reconcile.</param>
    /// <param name="consumerFoldedCount">
    /// The consumer-reported count of events it actually folded — required when the contract opts into
    /// <see cref="ReconciliationPatterns.EventCount"/>. For the engine's own projection it is the drain's
    /// running tally; an external consumer self-reports it. Ignored when the contract opts out of event-count.
    /// </param>
    public async Task<ConsumerReconciliationReport> ReconcileAsync(
        ReconciliationContract contract,
        Guid streamId,
        long consumerFoldedCount = 0,
        CancellationToken ct = default)
    {
        contract.EnsureValid();

        ChecksumReconciliation? checksum = null;
        if (contract.Patterns.HasFlag(ReconciliationPatterns.Checksum))
        {
            checksum = await ChecksumAsync(streamId, contract.ProjectionKind, ct);

            // Observation boundary (M.5 / ADR-IC-007 Layer 1): a mismatch that ran under a declared
            // contract is the §7.1 (a) alertable finding — emit it tagged by the consumer reference.
            // The ChecksumAsync fold itself stays a pure, replayable computation; the metric is a
            // side-effect here, not inside the fold (ADR-PC-010 §P5).
            if (!checksum.Match)
            {
                ReconciliationMetrics.RecordChecksumMismatch(contract.Consumer, contract.ProjectionKind);
            }
        }

        EventCountReconciliation? eventCount = null;
        if (contract.Patterns.HasFlag(ReconciliationPatterns.EventCount))
        {
            eventCount = await EventCountAsync(streamId, contract.ProjectionKind, consumerFoldedCount, ct);

            // §7.1 (b): only a SKIP (events lost) is alertable — a benign Gap is acceptable async lag
            // and is deliberately NOT counted, so the series is a clean skip signal.
            if (eventCount.Status == EventCountStatus.Skip)
            {
                ReconciliationMetrics.RecordEventCountDrift(contract.Consumer, contract.ProjectionKind);
            }
        }

        return new ConsumerReconciliationReport(contract, streamId, checksum, eventCount);
    }

    /// <summary>
    /// Cold-folds a stream's full belief from the event log alone — the same accumulating fold the
    /// projection runner materialises, computed independently here. Mirrors
    /// <see cref="ProjectionRunner{TState}.ApplyAsync"/>: an event type the projection does not handle
    /// leaves the state unchanged.
    /// </summary>
    private async Task<TState> FoldFromLogAsync(Guid streamId, CancellationToken ct)
    {
        var state = seed();
        await foreach (var envelope in eventStore.LoadAsync(streamId, fromSequence: 0, ct))
        {
            if (!handlers.TryResolveByEventType(envelope.EventType, out var registration))
            {
                continue;
            }

            var @event = eventSerializer.Decode(envelope.Payload, registration.PayloadType);
            state = (TState)registration.Handler.ApplyBoxed(state, @event).NewState;
        }

        return state;
    }

    private string HashState(TState state) => HashBytes(stateSerializer.Serialize(state));

    private static string HashBytes(ReadOnlySpan<byte> bytes)
    {
        Span<byte> digest = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(bytes, digest);
        return Convert.ToHexStringLower(digest);
    }
}

/// <summary>
/// The reconciliation-result instruments on the shared <see cref="BabelstoneTelemetry.Meter"/>
/// (ADR-IC-007 Layer 1, M.5): the three §7.1 alertable findings as monotonic
/// counters, plus the §7.2 drill-freshness observable gauge. They are the operational SIDE-EFFECT of
/// the reconciler's verdicts — emitted at the impure observation boundary, never inside a pure fold
/// (ADR-PC-010 §P5) — so the <c>projection-reconciliation</c> alert rules
/// (<c>infra/grafana/prometheus/alert-rules.yaml</c>) resolve to live series.
/// </summary>
/// <remarks>
/// <para>
/// Non-generic on purpose: <see cref="ProjectionReconciler{TState}"/> closes over a family's state
/// type, but the metrics are family-agnostic dimensions (<c>consumer</c> / <c>projection_kind</c>
/// REFERENCES, never PII — ADR-PC-004 §P2 / catalogue OBS_NO_PII_ATTRS). Registering them here means
/// ONE instrument per name on the shared meter regardless of how many closed reconciler types a host
/// instantiates. A host turns them on with <c>AddMeter(BabelstoneTelemetry.MeterName)</c>; with no
/// listener attached <see cref="Counter{T}.Add(T, KeyValuePair{string, object?}[])"/> and the gauge
/// callback are near-zero-cost no-ops.
/// </para>
/// <para>
/// The freshness gauge reports the Unix-epoch seconds of the most recent SUCCESSFUL in-process
/// rebuild drill per <c>projection_kind</c>, observed each OTel collection cycle from a process-wide
/// table the drill boundary updates. It carries the SAME name the projection-rebuild-drill script
/// pushes externally (<c>reconciliation_drill_last_success_timestamp_seconds</c>), so the
/// <c>ProjectionRebuildDrillStale</c> alert reads either source uniformly. The timestamp is read once,
/// at the boundary, via <see cref="DateTimeOffset.UtcNow"/> — outside any handler/fold, so handler
/// purity (BENG analysers) and replay determinism are unaffected.
/// </para>
/// </remarks>
internal static class ReconciliationMetrics
{
    private static readonly Counter<long> ChecksumMismatch =
        BabelstoneTelemetry.Meter.CreateCounter<long>(
            BabelstoneAttributes.ReconciliationChecksumMismatchMetric,
            description: "Projection state-checksum mismatches (event-store §7.1 (a) — consumer drift from a cold fold of the log).");

    private static readonly Counter<long> EventCountDrift =
        BabelstoneTelemetry.Meter.CreateCounter<long>(
            BabelstoneAttributes.ReconciliationEventCountDriftMetric,
            description: "Projection event-count SKIPs (event-store §7.1 (b) — consumer advanced past events it never folded; a benign Gap is not counted).");

    private static readonly Counter<long> RebuildDrillDivergence =
        BabelstoneTelemetry.Meter.CreateCounter<long>(
            BabelstoneAttributes.ReconciliationRebuildDrillDivergenceMetric,
            description: "Projection full-rebuild-drill divergences (event-store §7.2 (c) — a cold re-fold did not reproduce the running belief byte-for-byte).");

    // Last successful in-process rebuild drill per projection_kind, as Unix-epoch SECONDS. The
    // observable freshness gauge reads this table each collection cycle; the drill boundary writes it.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, double> LastDrillSuccessEpochSeconds = new();

    // Register the freshness gauge once, in the static initializer, on the shared meter. Observable
    // instruments are collected by the OTel cycle (or a test's RecordObservableInstruments()); with no
    // successful drill recorded yet the gauge emits nothing, and the alert's absent() reads that — the
    // safe "no recent drill" interpretation — until the first green drill lands.
    static ReconciliationMetrics() =>
        BabelstoneTelemetry.Meter.CreateObservableGauge(
            BabelstoneAttributes.ReconciliationDrillFreshnessMetric,
            observeValues: ObserveDrillFreshness,
            unit: "s",
            description: "Unix-epoch seconds of the most recent SUCCESSFUL projection-rebuild drill, per projection_kind (event-store §7.2 freshness SLI).");

    public static void RecordChecksumMismatch(string consumer, string projectionKind) =>
        ChecksumMismatch.Add(1, ConsumerTags(consumer, projectionKind));

    public static void RecordEventCountDrift(string consumer, string projectionKind) =>
        EventCountDrift.Add(1, ConsumerTags(consumer, projectionKind));

    public static void RecordRebuildDivergence(string projectionKind) =>
        RebuildDrillDivergence.Add(1, KindTag(projectionKind));

    public static void RecordDrillSuccess(string projectionKind) =>
        LastDrillSuccessEpochSeconds[projectionKind] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;

    private static IEnumerable<Measurement<double>> ObserveDrillFreshness() =>
        LastDrillSuccessEpochSeconds.Select(kv =>
            new Measurement<double>(kv.Value, KindTag(kv.Key)));

    private static KeyValuePair<string, object?>[] ConsumerTags(string consumer, string projectionKind) =>
    [
        new(BabelstoneAttributes.ReconciliationConsumer, consumer),
        new(BabelstoneAttributes.ProjectionKind, projectionKind),
    ];

    private static KeyValuePair<string, object?>[] KindTag(string projectionKind) =>
        [new(BabelstoneAttributes.ProjectionKind, projectionKind)];
}
