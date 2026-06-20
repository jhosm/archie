using System.Text.Json;
using Babelstone.Engine;
using Babelstone.EventStore;
using Babelstone.Families.TermDeposit;
using Babelstone.FinancialTypes;
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

    // The L.5 snapshot-accelerated path takes a snapshot every N events on the deep measurement stream
    // (ADR-PC-003 §P2 per-N trigger). A SMALL N (relative to the deep stream's length) guarantees a
    // snapshot lands well before the head, so the accelerated LoadAsync skips a measurable tail of events
    // — the whole point of measuring acceleration. Production uses 100 (event-store §8.1); the harness
    // builds a deep-enough stream that even 100 leaves a skippable tail, but a tighter rig N makes the
    // acceleration unambiguous on a modest measurement stream.
    private const long SnapshotEveryNEvents = 16;

    private readonly PostgresEventStore _store;
    private readonly EventStoreSink _sink;
    private readonly SelfDescribingJsonEventSerializer _serializer = new();
    private readonly HandlerRegistry _handlers = TermDepositFamilyModule.Registry();
    private readonly JsonStateSerializer<DepositPosition> _stateSerializer = new();
    private readonly AggregateRuntime<DepositPosition> _runtime;
    // A SECOND runtime wired WITH the snapshot store + the per-N policy, so an append on it writes
    // snapshots post-commit and a LoadAsync on it rehydrates snapshot-then-tail (the §P3 accelerated
    // fold). Kept separate from the latency-measuring _runtime so the §8.3 sync-band path is unchanged.
    private readonly AggregateRuntime<DepositPosition> _snapshotRuntime;
    // A runtime wired with NO snapshot store — its LoadAsync always folds cold from sequence 0, the
    // §P3 correctness fallback the accelerated state is compared against (snapshots accelerate, never lie).
    private readonly AggregateRuntime<DepositPosition> _coldRuntime;
    private readonly SnapshotStore<DepositPosition> _snapshotStore;
    private readonly PostgresSnapshotStore _snapshotStorage;
    private readonly ProjectionStore<DepositPosition> _projectionStore;
    private readonly ProjectionDrainer _drainer;
    private readonly ProjectionReconciler<DepositPosition> _reconciler;
    private readonly Func<IProjectionRunner> _runnerFactory;
    private readonly TimeProvider _clock;
    private readonly Guid _runNonce;
    private readonly string _connectionString;
    private readonly List<Guid> _appendedStreams = [];

    public EngineProjectionRig(string postgresConnectionString, TimeProvider clock, Guid runNonce)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(postgresConnectionString);
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _connectionString = postgresConnectionString;
        // The per-run nonce namespaces stream ids so a repeated run (even with the SAME seed, which by
        // §8.5 design reproduces the same synthetic deposit ids) does not collide with streams a prior
        // run already appended (an optimistic-concurrency ConcurrencyException). The run remains
        // reproducible from (seed, run-id, revision): the same nonce + seed yield the same stream ids.
        _runNonce = runNonce;

        _store = new PostgresEventStore(postgresConnectionString);
        _sink = new EventStoreSink(_store);
        var stateSerializer = _stateSerializer;
        var projectionStorage = new PostgresProjectionStore(postgresConnectionString);
        var checkpoints = new PostgresProjectionCheckpointStore(postgresConnectionString);
        _projectionStore = new ProjectionStore<DepositPosition>(projectionStorage, stateSerializer);

        _runtime = new AggregateRuntime<DepositPosition>(
            _store, _sink, _handlers, _serializer, new NullPiiProtector(), clock,
            () => DepositPosition.Empty);

        // The snapshot store (the SAME PostgresSnapshotStore the engine host wires, ADR-PC-003 §Decision)
        // and the per-N composing policy (ADR-PC-003 §P2 — the same CountBasedSnapshotPolicy the
        // production host injects, just a tighter rig threshold). A snapshot-write failure is fail-soft
        // (§P2: the commit is the book of record, the next rebuild is slower not wrong) — the rig has no
        // logger, so it rethrows to surface a genuine rig bug rather than silently masking a missing
        // snapshot that would make the L.5 acceleration look vacuous.
        _snapshotStorage = new PostgresSnapshotStore(postgresConnectionString);
        _snapshotStore = new SnapshotStore<DepositPosition>(_snapshotStorage, stateSerializer);
        _snapshotRuntime = new AggregateRuntime<DepositPosition>(
            _store, _sink, _handlers, _serializer, new NullPiiProtector(), clock,
            () => DepositPosition.Empty,
            snapshots: _snapshotStore,
            snapshotPolicy: new CountBasedSnapshotPolicy(SnapshotEveryNEvents));
        _coldRuntime = new AggregateRuntime<DepositPosition>(
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

    /// <summary>
    /// The L.5 deep-stream builder (bd babelstone-0uau.1): opens ONE stream with a constitution event and
    /// appends a tail of <c>InterestAccrued</c> events through the SNAPSHOT-wired runtime, so the engine's
    /// per-N policy (ADR-PC-003 §P2) writes snapshots mid-stream. A deep single stream is what makes
    /// snapshot acceleration measurable — a one-event stream has no tail to skip. Returns the deep stream's
    /// id and its head sequence.
    /// </summary>
    /// <param name="depth">
    /// How many events deep the stream is (1 constitution + depth-1 accruals). Must exceed
    /// <see cref="SnapshotEveryNEvents"/> so at least one snapshot lands before the head and the
    /// accelerated fold has a tail to skip.
    /// </param>
    /// <param name="seed">Seed for the constitution event (a fresh synthetic deposit, §8.5 reproducible).</param>
    public async Task<(Guid StreamId, long Head)> PopulateDeepStreamAsync(
        int depth, int seed, CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(depth, 2);

        // One synthetic constitution opens the deep stream; rebind its deposit id to the run-namespaced
        // stream id exactly as AppendWithSpanAsync does (the engine keys the stream by the aggregate id).
        var constitution = NewConstitution(seed);
        var streamId = RunStreamId(constitution.PartitionKey);
        var opened = constitution.Event is DepositConstituted c ? c with { DepositId = streamId } : constitution.Event;

        var context = DeepStreamContext(constitution.EmitInstant);
        var head = await _snapshotRuntime.AppendAsync(streamId, expectedVersion: -1, new[] { opened }, context, ct);

        // A tail of ordinary (non-lifecycle) InterestAccrued events deepens the stream so the per-N policy
        // fires. Each accrual is a pure fold on DepositPosition (Active deposit) — the harness appends the
        // raw events through the runtime (the family decider/legality table is a command-layer concern,
        // not the append path), so this stays a deterministic, seeded fold the cold path reproduces.
        for (var i = 1; i < depth; i++)
        {
            ct.ThrowIfCancellationRequested();
            // A deterministic 1-cent accrual per leg keeps the seeded state reproducible (§8.5) and the
            // money exact (cents in, cents out — Money.FromCents rounds once at the boundary, ADR-PC-010 §P2).
            var accrual = new InterestAccrued(Money.FromCents(1), new DateOnly(2026, 1, 1).AddDays(i));
            head = await _snapshotRuntime.AppendAsync(streamId, expectedVersion: head, new[] { accrual }, context, ct);
        }

        _appendedStreams.Add(streamId);
        return (streamId, head);
    }

    /// <summary>
    /// The L.5 snapshot-accelerated replay measurement (bd babelstone-0uau.1 / ADR-PC-003 §P3): over the
    /// SAME deep stream, times a COLD rebuild (snapshot-free, from sequence 0) and a SNAPSHOT-ACCELERATED
    /// rebuild (snapshot-then-tail via the snapshot-wired runtime), and confirms the two produce
    /// BYTE-IDENTICAL state. Returns the cold time, the snapshot time, whether the states matched, how many
    /// snapshots the accelerated path read, and the cold path's refolded event count.
    /// </summary>
    /// <remarks>
    /// In plain English: rebuild the same account two ways — the slow way (replay every event) and the fast
    /// way (start from a saved snapshot, replay only what came after) — and prove they land on the exact
    /// same answer, while timing both. A faster-but-different answer is the worst snapshot bug, so identity
    /// is what the verdict gates on; the speedup is reported. Both folds run through the engine's OWN
    /// AggregateRuntime.LoadAsync, so the bytes are production's, not a parallel re-implementation.
    /// </remarks>
    public async Task<(double ColdMs, double SnapshotMs, bool Identical, int SnapshotsApplied, int EventsRefolded)>
        MeasureSnapshotAcceleratedReplayAsync(Guid streamId, CancellationToken ct = default)
    {
        // Count the stream's events (the work the speedup is measured over) and how many snapshots the
        // accelerated path could draw on — zero snapshots means the acceleration cannot engage, which the
        // verdict treats as an explicit FAIL (a speedup claim over no snapshot is vacuous).
        var eventCount = 0;
        await foreach (var _ in _store.LoadAsync(streamId, fromSequence: 0, ct))
        {
            eventCount++;
        }

        var latestSnapshot = await _snapshotStorage.TryGetLatestAsync(streamId, ct);
        var snapshotsApplied = latestSnapshot is null ? 0 : 1;

        // COLD: the from-zero fold through a runtime with NO snapshot store — the §P3 correctness baseline.
        var coldSw = System.Diagnostics.Stopwatch.StartNew();
        var cold = await _coldRuntime.LoadAsync(streamId, ct);
        coldSw.Stop();

        // SNAPSHOT-ACCELERATED: the same LoadAsync through the snapshot-wired runtime — seeds from the
        // latest verified snapshot, then folds only the un-snapshotted tail (ADR-PC-003 §P3).
        var snapSw = System.Diagnostics.Stopwatch.StartNew();
        var accelerated = await _snapshotRuntime.LoadAsync(streamId, ct);
        snapSw.Stop();

        // Byte-identity over the SAME state serializer (the SnapshotEquivalenceProperties invariant): the
        // snapshot-then-tail state must hash-equal the cold-fold state, or the snapshot lied (§P3/§P4).
        var coldHash = HashState(cold.State);
        var acceleratedHash = HashState(accelerated.State);
        var identical = string.Equals(coldHash, acceleratedHash, StringComparison.Ordinal)
            && cold.Version == accelerated.Version;

        return (coldSw.Elapsed.TotalMilliseconds, snapSw.Elapsed.TotalMilliseconds, identical, snapshotsApplied, eventCount);
    }

    /// <summary>
    /// The L.6 discard primitive (bd babelstone-0uau.2 / ADR-PC-003 §8.3): deletes EVERY snapshot for the
    /// family's streams, so a subsequent rebuild re-folds cold with no snapshot to lean on — the
    /// discard-and-rebuild drill that proves the snapshots were faithful (and clears any PII materialised
    /// into a stale snapshot, ADR-PC-004 / §P4). Returns how many snapshot rows were discarded.
    /// </summary>
    public async Task<int> DiscardAllSnapshotsAsync(CancellationToken ct = default)
    {
        var streamIds = await _store.ReadStreamIdsAsync(Family, ct);
        var discarded = 0;
        foreach (var id in streamIds)
        {
            discarded += await _snapshotStorage.DiscardAsync(id, ct);
        }

        return discarded;
    }

    /// <summary>How many snapshot rows exist across the family's streams (the §P6 snapshot-lag input).</summary>
    public async Task<int> CountSnapshotsAsync(CancellationToken ct = default)
    {
        var streamIds = await _store.ReadStreamIdsAsync(Family, ct);
        var count = 0;
        foreach (var id in streamIds)
        {
            if (await _snapshotStorage.TryGetLatestAsync(id, ct) is not null)
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>
    /// The L.3e synchronous-replication append-latency measurement (bd babelstone-2e6q.5 / ADR-PC-005 §P1):
    /// times <paramref name="samples"/> appends on fresh streams with PostgreSQL <c>synchronous_commit</c>
    /// set OFF (<c>local</c> — the primary acknowledges without waiting on the standby) and again with it
    /// set ON (<c>on</c> — a commit blocks until the named standby flushed the WAL), and reports the p50/p99
    /// of each side. The delta is the write-path cost the RPO≈0 guarantee imposes — the §P1 claim the ADR
    /// requires the harness to measure, not assume.
    /// </summary>
    /// <remarks>
    /// In plain English: this times how long a write takes when the database is told "don't return until a
    /// standby copy has it" versus when it isn't, so we can report the safety guarantee's real cost. The
    /// honest measurement needs a real warm standby (the HA k8s overlay) — against the single-node dev
    /// stack the "on" side has no second node to wait on, so the number is a FLOOR, and the caller marks
    /// the verdict advisory in that case (the live-cluster cost is the residual ADR-PC-005 §P1 budget).
    /// </remarks>
    /// <param name="samples">How many appends to time per side (the p99 sample depth).</param>
    /// <param name="seed">Seed for the synthetic deposits appended (each its own stream, §8.5 reproducible).</param>
    public async Task<(double OffP50, double OffP99, double OnP50, double OnP99)> MeasureReplicationLatencyAsync(
        int samples, int seed, CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(samples);

        // OFF first (relaxed durability), then ON (wait-for-standby): two disjoint batches of fresh
        // single-event streams, so neither side ever contends on the optimistic-concurrency head.
        var off = await TimeAppendsAsync(samples, seed, synchronousCommit: "local", ct);
        var on = await TimeAppendsAsync(samples, seed ^ unchecked((int)0x5147_0001), synchronousCommit: "on", ct);

        return (Percentile(off, 0.50), Percentile(off, 0.99), Percentile(on, 0.50), Percentile(on, 0.99));
    }

    // Time `samples` single-event appends, each opening a fresh stream, with synchronous_commit forced to
    // `synchronousCommit` for every connection the store opens. The toggle is applied via the Npgsql
    // `Options` connection-string keyword (a libpq session GUC: `-c synchronous_commit=…`) rather than by
    // reaching into the engine's store — the store opens a fresh connection per append from its connection
    // string, so a store built against a synchronous_commit-pinned string commits every append under that
    // setting. synchronous_commit=on against a NAMED standby (synchronous_standby_names, set on the HA
    // primary) is the §P1 wait-for-standby cost; =local relaxes it to a local flush. The engine core is
    // untouched — this is a pure connection-string composition the rig owns.
    private async Task<double[]> TimeAppendsAsync(
        int samples, int seed, string synchronousCommit, CancellationToken ct)
    {
        var pinnedStore = new PostgresEventStore(WithSynchronousCommit(_connectionString, synchronousCommit));
        var pinnedSink = new EventStoreSink(pinnedStore);
        var pinnedRuntime = new AggregateRuntime<DepositPosition>(
            pinnedStore, pinnedSink, _handlers, _serializer, new NullPiiProtector(), _clock,
            () => DepositPosition.Empty);

        var timings = new double[samples];
        for (var i = 0; i < samples; i++)
        {
            ct.ThrowIfCancellationRequested();
            var synthetic = NewConstitution(seed + i);
            var streamId = RunStreamId(synthetic.PartitionKey);
            var opened = synthetic.Event is DepositConstituted c ? c with { DepositId = streamId } : synthetic.Event;
            var context = DeepStreamContext(synthetic.EmitInstant);

            var sw = System.Diagnostics.Stopwatch.StartNew();
            await pinnedRuntime.AppendAsync(streamId, expectedVersion: -1, new[] { opened }, context, ct);
            sw.Stop();
            timings[i] = sw.Elapsed.TotalMilliseconds;
        }

        Array.Sort(timings);
        return timings;
    }

    // Compose a connection string that pins synchronous_commit for every session opened from it, via the
    // Npgsql `Options` keyword (passed to libpq as `-c synchronous_commit=<value>`). Any existing Options
    // is preserved. Pure string composition — no engine-core change.
    internal static string WithSynchronousCommit(string connectionString, string value)
    {
        var builder = new Npgsql.NpgsqlConnectionStringBuilder(connectionString);
        var option = $"-c synchronous_commit={value}";
        builder.Options = string.IsNullOrEmpty(builder.Options) ? option : $"{builder.Options} {option}";
        return builder.ConnectionString;
    }

    // Nearest-rank percentile over a sorted array (the same no-interpolation convention
    // LatencyObserver.Percentile uses in the library, restated here because that helper is internal to a
    // different assembly — so every harness measurement path agrees on what "p99" means).
    private static double Percentile(double[] sorted, double q)
    {
        if (sorted.Length == 0)
        {
            return 0;
        }

        if (sorted.Length == 1)
        {
            return sorted[0];
        }

        var rank = (int)Math.Ceiling(q * sorted.Length);
        var index = Math.Clamp(rank - 1, 0, sorted.Length - 1);
        return sorted[index];
    }

    private string HashState(DepositPosition state)
    {
        Span<byte> digest = stackalloc byte[32];
        System.Security.Cryptography.SHA256.HashData(_stateSerializer.Serialize(state), digest);
        return Convert.ToHexStringLower(digest);
    }

    // A fresh seeded constitution event (the WorkloadGenerator's first emitted constitution for `seed`).
    private static SyntheticEvent NewConstitution(int seed)
    {
        var generator = new WorkloadGenerator(seed, WorkloadSpec.Default(), Calibration.V4Placeholder());
        return generator
            .Generate(1, new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), TimeSpan.FromHours(24), new DateOnly(2026, 11, 27))
            .First();
    }

    private static AppendContext DeepStreamContext(DateTimeOffset validTime) => new(
        Family: Family,
        PackVersion: "pt.2026.1",
        SchemaVersion: "term_deposit@2026.1",
        Actor: "load-harness",
        ValidTime: validTime);

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
