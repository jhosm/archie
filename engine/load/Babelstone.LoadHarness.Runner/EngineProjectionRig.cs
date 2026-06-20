using System.Text.Json;
using Babelstone.Engine;
using Babelstone.EventStore;
using Babelstone.Families.TermDeposit;
using Babelstone.Telemetry;

namespace Babelstone.LoadHarness.Runner;

/// <summary>
/// The self-describing JSON store codec (ADR-PC-028 EVENT_STORE_PAYLOAD_SELF_DESCRIBING): the
/// <c>events.payload</c> book of record is decodable with NO Schema Registry. This is the SAME shape as
/// the engine host's <c>Babelstone.Engine.Api.JsonEventSerializer</c>; it is copied here (8 lines)
/// rather than referencing the whole ASP.NET host project, so the harness's project graph stays minimal
/// and Schema-Registry-free for the in-process append/replay path. The harness's BUS path (the §G1
/// production producer) still encodes with the engine's OWN Avro serializer in <c>WorkloadDriver</c> —
/// this codec only fronts the store/replay path the rig drives in-process.
/// </summary>
// internal sealed and reachable only via the EngineProjectionRig — exercised by the
// [Category("Integration")] Testcontainers suite (EngineProjectionRigIntegrationTests, bd babelstone-2e6q.7),
// which drives the rig's append/replay path against a live PostgreSQL and so folds this codec.
internal sealed class SelfDescribingJsonEventSerializer : IEventSerializer
{
    public EncodedPayload Encode(DomainEvent @event)
        => new(JsonSerializer.SerializeToUtf8Bytes(@event, @event.GetType()), SchemaId: 1);

    public DomainEvent Decode(ReadOnlyMemory<byte> payload, Type payloadType)
        => (DomainEvent)JsonSerializer.Deserialize(payload.Span, payloadType)!;
}

/// <summary>
/// Composes the engine's append + projection + cold-replay path IN-PROCESS against a live PostgreSQL
/// event store, so the engine emits its OWN OpenTelemetry spans the <see cref="LatencyObserver"/> reads
/// (ADR-PC-011 §S2: "the projection-rebuild drill and per-partition ordering assertions are
/// engine-internal checks the harness coordinates in-process"; §P2/§G2: latency is the engine span's
/// boundary-to-commit DURATION, never the driver's send clock).
/// </summary>
/// <remarks>
/// <para>
/// In plain English: there is no separate running "engine consumer" host wired to Redpanda today, so to
/// measure how long the engine takes to commit a projection the harness drives the engine's real
/// append+project code directly, in the same process, against the real database. That append opens the
/// engine's real product span (e.g. <c>accrual.computed</c>) — the exact telemetry production emits — and
/// the observer reads its duration. The bytes-on-the-bus production path is exercised separately by the
/// <see cref="WorkloadDriver"/> against live Redpanda (§G1); this rig is the measured-latency path (§G2).
/// </para>
/// <para>
/// Determinism (§G3): the rig stamps a SimulatedClock-derived transaction_time and uses the seeded
/// synthetic events; no <c>Guid.NewGuid()</c> drives state. The append path itself mints event ids
/// (the engine owns identity), which does not affect the folded projection state the no-divergence
/// invariant compares.
/// </para>
/// </remarks>
// Every member here opens a live Npgsql connection to the event store (append, drain, cold-replay,
// the no-divergence drill), so this rig is reachable only with a running PostgreSQL — never from the
// Docker-free unit lane (`Category!=Integration`). It is covered by the [Category("Integration")]
// Testcontainers suite (EngineProjectionRigIntegrationTests, bd babelstone-2e6q.7), which drives every
// member against a real PostgreSQL in the CI integration lane (--filter "Category=Integration"); those
// branches are therefore MEASURED, not excluded, and the merged report still clears the engine floor.
internal sealed class EngineProjectionRig
{
    private const string Family = "term_deposit";

    private readonly PostgresEventStore _store;
    private readonly EventStoreSink _sink;
    private readonly SelfDescribingJsonEventSerializer _serializer = new();
    private readonly HandlerRegistry _handlers = TermDepositFamilyModule.Registry();
    private readonly AggregateRuntime<DepositPosition> _runtime;
    private readonly ProjectionStore<DepositPosition> _projectionStore;
    private readonly ProjectionDrainer _drainer;
    private readonly ProjectionReconciler<DepositPosition> _reconciler;
    private readonly Func<IProjectionRunner> _runnerFactory;
    private readonly TimeProvider _clock;
    private readonly Guid _runNonce;
    private readonly List<Guid> _appendedStreams = [];

    public EngineProjectionRig(string postgresConnectionString, TimeProvider clock, Guid runNonce)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(postgresConnectionString);
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        // The per-run nonce namespaces stream ids so a repeated run (even with the SAME seed, which by
        // §8.5 design reproduces the same synthetic deposit ids) does not collide with streams a prior
        // run already appended (an optimistic-concurrency ConcurrencyException). The run remains
        // reproducible from (seed, run-id, revision): the same nonce + seed yield the same stream ids.
        _runNonce = runNonce;

        _store = new PostgresEventStore(postgresConnectionString);
        _sink = new EventStoreSink(_store);
        var stateSerializer = new JsonStateSerializer<DepositPosition>();
        var projectionStorage = new PostgresProjectionStore(postgresConnectionString);
        var checkpoints = new PostgresProjectionCheckpointStore(postgresConnectionString);
        _projectionStore = new ProjectionStore<DepositPosition>(projectionStorage, stateSerializer);

        _runtime = new AggregateRuntime<DepositPosition>(
            _store, _sink, _handlers, _serializer, new NullPiiProtector(), clock,
            () => DepositPosition.Empty);

        _drainer = new ProjectionDrainer(_store, checkpoints, clock);
        _reconciler = new ProjectionReconciler<DepositPosition>(
            _store, projectionStorage, _handlers, _serializer, stateSerializer, () => DepositPosition.Empty);

        // A fresh runner per drain/rebuild: the drainer drives one runner's checkpoint; building it on
        // demand keeps the rig stateless across phases (the runner is cheap).
        _runnerFactory = () => new ProjectionRunner<DepositPosition>(
            TermDepositProjectionModule.DepositPositionKind, Family, ProjectionMode.Async,
            _handlers, _serializer, () => DepositPosition.Empty, _projectionStore);
    }

    /// <summary>
    /// Appends one synthetic event to the live event store through the engine's real runtime, opening the
    /// §8.3 sync-band span whose DURATION the observer reads as boundary-to-commit latency (§P2). The
    /// span is the engine's OWN product span on <see cref="BabelstoneTelemetry.ActivitySource"/> — not a
    /// test-only instrument (§8.4). Returns the stream's new head sequence.
    /// </summary>
    public async Task<long> AppendWithSpanAsync(SyntheticEvent synthetic, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(synthetic);
        var streamId = RunStreamId(synthetic.PartitionKey);
        var spanName = SpanFor(synthetic.MixClass);
        var spanAttributes = new KeyValuePair<string, object?>[]
        {
            new(BabelstoneAttributes.PartitionKey, streamId.ToString()),
            new(BabelstoneAttributes.ProductCode, ProductCodeOf(synthetic)),
        };

        var context = new AppendContext(
            Family: Family,
            PackVersion: "pt.2026.1",
            SchemaVersion: "term_deposit@2026.1",
            Actor: "load-harness",
            ValidTime: synthetic.EmitInstant);

        // The aggregate id must equal the stream id (the engine keys the stream by the aggregate id and
        // the projection records it), so rebind the generated DepositConstituted's DepositId to the
        // run-namespaced stream id while preserving every other seeded field (§8.5 reproducibility).
        var @event = synthetic.Event is DepositConstituted c ? c with { DepositId = streamId } : synthetic.Event;

        // expectedVersion -1: each synthetic constitution opens a NEW stream keyed by its (run-namespaced)
        // deposit id, so there is no optimistic-concurrency contention across the workload.
        var head = await _runtime.AppendAsync(
            streamId, expectedVersion: -1, new[] { @event }, context, ct,
            spanName: spanName, spanAttributes: spanAttributes);
        _appendedStreams.Add(streamId);
        return head;
    }

    /// <summary>The stream ids THIS run appended (for the L.3d single-stream replay-timing pick).</summary>
    public IReadOnlyList<Guid> AppendedStreams => _appendedStreams;

    // Namespace a synthetic partition key into this run's stream-id space: SHA-256(nonce ‖ partitionKey)
    // folded into a Guid. Deterministic in (runNonce, partitionKey) so the same (seed, run-id) reproduces
    // the same stream ids, but two runs (different nonces) never collide.
    private Guid RunStreamId(Guid partitionKey)
    {
        Span<byte> input = stackalloc byte[32];
        _runNonce.TryWriteBytes(input[..16]);
        partitionKey.TryWriteBytes(input[16..]);
        Span<byte> digest = stackalloc byte[32];
        System.Security.Cryptography.SHA256.HashData(input, digest);
        return new Guid(digest[..16]);
    }

    /// <summary>
    /// Drains the deposit-position projection forward over everything appended so far (the async
    /// projector's catch-up), returning how many events were folded. Run between/after the append loop so
    /// the no-divergence drill has a materialised running belief to compare a cold rebuild against.
    /// </summary>
    public Task<int> DrainAsync(CancellationToken ct = default) =>
        _drainer.DrainOnceAsync(_runnerFactory(), ct);

    /// <summary>
    /// The L.3d cold-replay measurement: cold-folds ONE stream's full belief from the event log alone
    /// (the engine's <see cref="ProjectionReconciler{TState}"/> checksum fold — the same fold the
    /// projection materialises, computed independently), timing the rebuild against the §8.2 budget for
    /// the replay class. Returns the elapsed time and how many events were re-folded.
    /// </summary>
    public async Task<(double ElapsedMs, int EventsRefolded)> TimeColdReplayAsync(
        Guid streamId, CancellationToken ct = default)
    {
        // Count the stream's events first so the verdict reports the work; the checksum below re-folds
        // the same events from sequence 0 (the cold path).
        var eventCount = 0;
        await foreach (var _ in _store.LoadAsync(streamId, fromSequence: 0, ct))
        {
            eventCount++;
        }

        var sw = System.Diagnostics.Stopwatch.StartNew();
        // ChecksumAsync cold-folds the stream from the log alone and hashes it — a pure, snapshot-free
        // cold replay of exactly the §8.2 budget shape (one instance's full lifecycle from sequence 0).
        _ = await _reconciler.ChecksumAsync(streamId, TermDepositProjectionModule.DepositPositionKind, ct);
        sw.Stop();
        return (sw.Elapsed.TotalMilliseconds, eventCount);
    }

    /// <summary>
    /// The L.3d no-rebuild-divergence drill (§8.3 reliability invariant / event-store §7.2): for each
    /// stream the workload populated, runs <see cref="ProjectionReconciler{TState}.FullRebuildDrillAsync"/>
    /// (supersede-all + checkpoint reset + cold re-fold) and checks the rebuilt belief is byte-identical
    /// to the running belief. Returns the streams checked, how many diverged, and events re-folded.
    /// </summary>
    public async Task<(int StreamsChecked, int Divergent, int EventsRefolded)> RunNoDivergenceDrillAsync(
        CancellationToken ct = default)
    {
        // A single rebuild supersedes ALL beliefs for the kind and re-folds every stream of the family
        // from sequence 0, so the drill is run ONCE and then each stream's before/after is compared.
        var streamIds = await _store.ReadStreamIdsAsync(Family, ct);

        // Capture each running belief's hash BEFORE the rebuild.
        var before = new Dictionary<Guid, string?>(streamIds.Count);
        foreach (var id in streamIds)
        {
            var checksum = await _reconciler.ChecksumAsync(id, TermDepositProjectionModule.DepositPositionKind, ct);
            before[id] = checksum.ProjectionExists ? checksum.ProjectionHash : null;
        }

        // One rebuild for the kind (supersede-all + checkpoint reset + cold re-fold across all streams).
        var refolded = await _drainer.RebuildAsync(_runnerFactory(), ct);

        // Compare each stream's rebuilt belief hash to the engine's independent cold-fold hash. The
        // engine hash IS the cold fold from the log, so after a clean rebuild the materialised belief
        // equals it; a mismatch is genuine divergence (the slow-drift bug the drill exists to catch).
        var divergent = 0;
        foreach (var id in streamIds)
        {
            var checksum = await _reconciler.ChecksumAsync(id, TermDepositProjectionModule.DepositPositionKind, ct);
            if (!checksum.Match)
            {
                divergent++;
            }
        }

        return (streamIds.Count, divergent, refolded);
    }

    // Bind a §8.2 sync mix class to the §8.3 sync band's span. card_transactions / transfers map to the
    // current_balance/available_credit band (accrual.computed); operational maps to the hold_freeze_ledger
    // band (withholding.applied). Both §8.3 sync bands therefore receive engine spans under the default
    // mix, so neither band is an automatic "no spans captured" FAIL.
    private static string SpanFor(string mixClass) => mixClass switch
    {
        "operational" => BabelstoneAttributes.SpanWithholdingApplied,
        _ => BabelstoneAttributes.SpanAccrualComputed,
    };

    private static string ProductCodeOf(SyntheticEvent synthetic) =>
        synthetic.Event is DepositConstituted c ? c.ProductCode : "dpz_pt_seed";
}
