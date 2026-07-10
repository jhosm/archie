namespace Babelstone.Telemetry;

/// <summary>
/// The versioned <c>babelstone.*</c> span-attribute key contract and the manual span names
/// (ADR-IC-007). These keys are a wire contract read by Grafana/Tempo queries and
/// catalogue fitness functions — <b>never rename a key</b>; add a new one and deprecate the old.
///
/// Every key here is in the ADR-IC-007 <i>operational</i> tier: structural identifiers only.
/// No NIF, IBAN, name, e-mail, or other personal/financial-restricted value may be carried under
/// these keys (catalogue <c>OBS_NO_PII_ATTRS</c> / ADR-PC-004). Money is carried as integer
/// cents under <see cref="InterestCents"/> / <see cref="TaxCents"/> — never a formatted decimal —
/// matching the engine's cents-native discipline.
/// </summary>
public static class BabelstoneAttributes
{
    /// <summary>The aggregate's partition key (v1: the stream id). Structural identifier, not PII.</summary>
    public const string PartitionKey = "babelstone.partition_key";

    /// <summary>The product code the command targets (e.g. a deposit product id). Structural, not PII.</summary>
    public const string ProductCode = "babelstone.product_code";

    /// <summary>Interest accrued, in integer cents (cents-native — never a formatted decimal).</summary>
    public const string InterestCents = "babelstone.interest_cents";

    /// <summary>Tax withheld, in integer cents (cents-native — never a formatted decimal).</summary>
    public const string TaxCents = "babelstone.tax_cents";

    /// <summary>The as-of date the computation is anchored to (a date, never a wall-clock-derived value at the call site).</summary>
    public const string AsOf = "babelstone.as_of";

    /// <summary>
    /// A PSEUDONYM for the customer a span needs to reference for debugging (ADR-IC-016 plane iii):
    /// a short, salted, one-way hash of the raw <c>client_id</c>, NOT the id itself. The raw id is
    /// PII (it keys into the Customer Data Store) and may never ride a telemetry signal; the
    /// pseudonym lets an operator correlate the spans of one customer's debugging session without the
    /// trace backend becoming a searchable index of personal data. It is reversible only inside the
    /// Customer Data Store that holds the same salt — by design (Document 10 Principle 4). Derive it
    /// with <see cref="ClientPseudonym.Of"/>; never set this key from a raw id. The key deliberately
    /// avoids the <c>client</c>/<c>account</c>/<c>name</c> PII fragments the OBS-3 structural
    /// assertion scans for: <c>subject_pseudonym</c> carries an opaque hash, never an identifier.
    /// </summary>
    public const string SubjectPseudonym = "babelstone.subject_pseudonym";

    /// <summary>Manual span name for a deposit constitution (ADR-IC-007 <c>&lt;entity&gt;.&lt;operation&gt;</c>).</summary>
    public const string SpanConstituted = "deposit.constituted";

    /// <summary>Manual span name for an interest-accrual computation (ADR-IC-007 <c>&lt;layer&gt;.&lt;operation&gt;</c>).</summary>
    public const string SpanAccrualComputed = "accrual.computed";

    /// <summary>Manual span name for a withholding-tax application (ADR-IC-007 <c>&lt;layer&gt;.&lt;operation&gt;</c>).</summary>
    public const string SpanWithholdingApplied = "withholding.applied";

    /// <summary>
    /// Manual span name for ONE lifecycle-command dispatch (ADR-PC-036 §Decision 2): the lifecycle
    /// driver's HTTP sink POSTs a single due command to the engine's ADR-PC-029 command surface. Opened in
    /// the impure sink shell (never a pure schedule pass or fold), it nests under the driver worker's
    /// per-tick <c>cadence.pass</c> span, so a tick's dispatches read as a connected chain in the trace.
    /// <c>&lt;entity&gt;.&lt;operation&gt;</c> per ADR-IC-007. It carries only structural tags
    /// (<see cref="PartitionKey"/> for the target stream, <see cref="LifecycleCommandKind"/>,
    /// <see cref="LifecycleOccurrenceKey"/>) — never PII (ADR-PC-004 / OBS_NO_PII_ATTRS).
    /// </summary>
    public const string SpanLifecycleDispatch = "lifecycle.dispatch";

    /// <summary>The STABLE lifecycle command-kind code the dispatch targets (e.g. <c>pay_installment</c>,
    /// <c>mature_deposit</c> — ADR-PC-036 §Decision 1). A kind name, never PII.</summary>
    public const string LifecycleCommandKind = "babelstone.lifecycle.command_kind";

    /// <summary>The STABLE per-occurrence key the dispatch is for (the installment number, or <c>1</c> for a
    /// one-shot maturity — ADR-PC-036 §Decision 3). A structural ordinal that makes a dispatch span
    /// idempotency-addressable; never PII.</summary>
    public const string LifecycleOccurrenceKey = "babelstone.lifecycle.occurrence_key";

    /// <summary>
    /// Manual span name for ONE saga-advance step (H.5): the orchestrator drives a saga forward
    /// from one inbound event, decides the legal transition (ADR-IC-003), and emits the
    /// decided commands. The span is opened in the impure advance shell, parented to the inbound
    /// event's W3C trace context (the <c>traceparent</c> header), so a saga's work shows up as a
    /// connected chain in the distributed trace (ADR-IC-007 Layer 1 — <c>traceparent</c> is the
    /// mechanism by which the identity trio becomes distributed tracing; Document 06 "each saga
    /// state transition" is a manual span). <c>&lt;entity&gt;.&lt;operation&gt;</c> per ADR-IC-007.
    /// </summary>
    public const string SpanSagaAdvance = "saga.advance";

    /// <summary>The saga instance reference the span is for (the Document 05 PROC-… id). Structural
    /// identifier, NOT PII — ADR-IC-003 requires <c>process_id</c> on every orchestrator span.</summary>
    public const string SagaProcessId = "babelstone.saga.process_id";

    /// <summary>Which state machine governs the advance (e.g. <c>ConstitutionProcess</c>). Structural,
    /// not PII.</summary>
    public const string SagaType = "babelstone.saga.type";

    /// <summary>The inbound event TYPE that drove the advance (e.g. <c>BalanceReserved</c>) — the key
    /// the transition table keys on (ADR-IC-003). A type name, never PII.</summary>
    public const string SagaEventType = "babelstone.saga.event_type";

    /// <summary>The saga state move this advance took, rendered <c>FROM-&gt;TO</c> (e.g.
    /// <c>PARALLEL_VALIDATION-&gt;AWAIT_LIMITS_VALIDATED</c>) — Document 06 "each saga state
    /// transition" as a span tag. The state names are operational, never PII.</summary>
    public const string SagaTransition = "babelstone.saga.transition";

    /// <summary>The advance disposition (<c>Started</c>/<c>Advanced</c>/<c>Duplicate</c>/… — the
    /// <c>AdvanceOutcome</c>), so the span records whether the step moved the saga, deduped, or was
    /// rejected. Operational, not PII.</summary>
    public const string SagaOutcome = "babelstone.saga.outcome";

    /// <summary>
    /// The CORRELATION reference (Primitive 4 / ADR-IC-003): the originating request's
    /// correlation id, carried UNCHANGED through the whole saga. ADR-IC-003 requires it on every
    /// orchestrator span so a saga is one searchable chain. It is a structural GUID reference, NOT
    /// PII — distinct from the OTel <c>trace_id</c> the span itself carries (the two are correlated
    /// in Grafana, ADR-IC-007). Pseudonymous by construction (Document 06).
    /// </summary>
    public const string SagaCorrelationId = "babelstone.saga.correlation_id";

    /// <summary>
    /// The CAUSATION reference (Primitive 4 / ADR-IC-003): the <c>message_id</c> (ce_id) of the
    /// inbound event that triggered this advance — the cause of the commands it emits. A pre-existing
    /// reference carried through, never minted. Structural, not PII.
    /// </summary>
    public const string SagaCausationId = "babelstone.saga.causation_id";

    /// <summary>
    /// The outbox publish-lag SLI (ADR-IC-004): an <i>observable gauge</i> of the age in seconds
    /// of the OLDEST <c>PENDING</c> outbox row at each collection cycle — <c>clock_timestamp() −
    /// MIN(created_at)</c> over PENDING rows, computed in the DB (single-clock; 0 when the backlog is
    /// empty). It keeps reporting (and climbing) even when nothing publishes, so the ADR-IC-004 Warning
    /// (&gt;30s) and Critical (&gt;5min "publisher not running or Redpanda unavailable") thresholds
    /// can fire during an outage — the exact failure mode the SLI exists to catch. The metric name is
    /// the ADR-IC-004 contract string — a Prometheus/Grafana query reads it by this exact name, so it follows
    /// snake_case-with-unit-suffix convention, never the <c>babelstone.*</c> span-key contract above.
    /// Warning/critical thresholds (30s / 5min) are deployment-time Grafana rules, not code.
    /// </summary>
    public const string OutboxPublishLagMetric = "outbox_publish_lag_seconds";

    /// <summary>
    /// The per-row outbox publish-<i>latency</i> histogram (a G.1 addition, NOT the ADR-IC-004 SLI): the
    /// seconds between a row's enqueue (<c>created_at</c>) and its successful publish ack
    /// (<c>published_at</c>), recorded once per published row, tagged by <see cref="AggregateType"/>.
    /// It measures end-to-end delivery latency for rows that DID publish; it is deliberately a
    /// DISTINCT name from <see cref="OutboxPublishLagMetric"/> so it does not shadow the ADR-IC-004 backlog-age
    /// gauge (a per-row metric goes silent during an outage — the opposite of what ADR-IC-004 needs). Computed
    /// single-clock in the DB (<c>published_at − created_at</c>, both DB-stamped) so host/DB clock skew
    /// cannot bias or negate it. snake_case-with-unit-suffix, not a <c>babelstone.*</c> span key.
    /// </summary>
    public const string OutboxPublishLatencyMetric = "outbox_publish_latency_seconds";

    /// <summary>
    /// The aggregate type the lagged row routes to (e.g. <c>term_deposit</c>) — the structural
    /// dimension the publish-lag histogram is tagged with so lag is breakable by topic. Operational
    /// tier, not PII; it is the same value carried as the row's <c>aggregate_type</c> / topic name.
    /// </summary>
    public const string AggregateType = "babelstone.aggregate_type";

    /// <summary>
    /// The topic an inbox record arrived on (e.g. <c>term_deposit</c>) — the structural dimension the
    /// consumer-side inbox counters (G.2) are tagged with so handled/duplicate/poison rates are
    /// breakable by topic. Operational tier, not PII; it is the same value as the producer's
    /// <c>aggregate_type</c> / topic name (the consumer mirror of <see cref="AggregateType"/>).
    /// </summary>
    public const string SourceTopic = "babelstone.source_topic";

    /// <summary>
    /// Commands APPLIED at the engine's command surface (bd babelstone-f0ic.15.6): a monotonic
    /// counter incremented once per command whose decided events COMMITTED through
    /// <c>AggregateRuntime.AppendAsync</c> — the impure runtime shell every family command funnels
    /// through, so one emit site covers every ingress (ADR-PC-029) without per-endpoint sprawl.
    /// A dedup replay does NOT count here (it appends nothing) — it counts on
    /// <see cref="CommandDedupHitsMetric"/> instead, so the two series partition retries from work.
    /// Tagged by <see cref="AggregateType"/> (the family). snake_case-with-<c>_total</c> — a
    /// Prometheus/Grafana query (the Mission Control Metrics lens) reads it by this exact string,
    /// never a <c>babelstone.*</c> span key.
    /// </summary>
    public const string CommandsMetric = "commands_total";

    /// <summary>
    /// Command-idempotency REPLAYS refused a second apply (ADR-PC-029 slot 4 doing its job;
    /// bd babelstone-f0ic.15.6): a monotonic counter incremented when a command id is found
    /// already-applied — either at the endpoint pre-check (the <c>ICommandLog</c> receipt read, the
    /// common sequential retry) or by the in-transaction <c>command_dedup</c> PK collision (the
    /// concurrent racer). The ledger row itself is a SILENT collision, so this counter is the ONLY
    /// visible trace of a dedup hit — a rising rate is at-least-once redelivery being absorbed
    /// (healthy in moderation; a dispatcher symptom if it dominates). Dimensionless (the pre-check
    /// site has no family in scope). snake_case metric name, not a span key.
    /// </summary>
    public const string CommandDedupHitsMetric = "command_dedup_hits_total";

    /// <summary>
    /// Immutable facts COMMITTED to the event log (bd babelstone-f0ic.15.6): a monotonic counter
    /// incremented by the batch size on each successful <c>AggregateRuntime.AppendAsync</c> commit —
    /// emitted in the runtime shell AFTER the sink transaction succeeds, never in a pure
    /// decider/fold and never on a rolled-back append (OBS_SPAN_PRODUCT_SEMANTICS / ADR-PC-010).
    /// Tagged by <see cref="AggregateType"/> (the family). snake_case metric name read by the
    /// Metrics lens by this exact string, not a span key.
    /// </summary>
    public const string EventsAppendedMetric = "events_appended_total";

    /// <summary>
    /// Hold-lifecycle releases that transitioned NOTHING (ADR-PC-033: a capture/expiry of an
    /// unplaced hold is a fold error, a duplicate release a reconciliation signal — both must be
    /// surfaced, not silently absorbed). A monotonic counter bumped by the active-hold projector
    /// when a <c>HoldCaptured</c>/<c>HoldExpired</c> folds as a no-op, tagged by
    /// <see cref="HoldReleaseAnomalyKindTag"/>. snake_case metric name (a Prometheus/Grafana query
    /// reads it by this exact string), not a span key.
    /// </summary>
    public const string HoldReleaseAnomaliesMetric = "hold_release_anomalies_total";

    /// <summary>
    /// The metric dimension classifying a no-op hold release (<c>never_placed</c> — the fold-order
    /// error — vs <c>already_released</c> — the duplicate/late release). A closed two-member set,
    /// operational tier only; the hold id itself rides the structured warning log, never a metric
    /// dimension (unbounded cardinality).
    /// </summary>
    public const string HoldReleaseAnomalyKindTag = "babelstone.hold_release_anomaly";

    /// <summary>
    /// Inbox messages handled for the FIRST time (G.2): the dedup row was inserted and the handler
    /// ran inside one transaction (Document 04). A monotonic counter, tagged by <see cref="SourceTopic"/>.
    /// Distinct from <see cref="InboxDuplicatesMetric"/> (the dedup-backstop firing). snake_case
    /// metric name (a Prometheus/Grafana query reads it by this exact string), not a span key.
    /// </summary>
    public const string InboxHandledMetric = "inbox_handled_total";

    /// <summary>
    /// Inbox messages skipped as DUPLICATE physical deliveries (G.2): the <c>message_id</c> PK
    /// collided, the transaction rolled back, no effect ran. This counter rising is the ADR-IC-004
    /// dedup backstop doing its mandatory job (absorbing the dual-publish window) —
    /// healthy in moderation, a producer/relay symptom if it dominates. Tagged by <see cref="SourceTopic"/>.
    /// </summary>
    public const string InboxDuplicatesMetric = "inbox_duplicates_total";

    /// <summary>
    /// Inbox records skipped as POISON (G.2): a record that cannot be processed (un-decodable Avro,
    /// unknown event type, bad wire framing, or a missing <c>ce_id</c>) and so is stepped past rather
    /// than wedging the partition. A non-zero rate warrants investigation (a contract/registration
    /// gap). Tagged by <see cref="SourceTopic"/>. snake_case metric name, not a span key.
    /// </summary>
    public const string InboxPoisonMetric = "inbox_poison_total";

    /// <summary>
    /// Inbox null-payload TOMBSTONES skipped (G.2): a Redpanda log-compaction tombstone (a record with
    /// a key but a null/empty value — the GDPR right-to-erasure signal on a <c>cleanup.policy=compact</c>
    /// topic, ADR-IC-001 / ADR-IC-002). It is committed-past WITHOUT being decoded as Avro, and
    /// counted here — DISTINCT from <see cref="InboxPoisonMetric"/> so a routine crypto-shred upstream
    /// never fires a false poison alert. A rising rate is the normal shape of erasure traffic, not a
    /// contract gap. Tagged by <see cref="SourceTopic"/>. snake_case metric name, not a span key.
    /// </summary>
    public const string InboxTombstoneMetric = "inbox_tombstone_total";

    /// <summary>
    /// Projection-reconciliation CHECKSUM MISMATCH (event-store §7.1 pattern (a), M.5): a monotonic
    /// counter incremented when a per-instance state checksum finds the
    /// projection's materialised belief disagreeing byte-for-byte with an independent cold fold of the
    /// event log (<c>ChecksumReconciliation.Match == false</c>) — consumer drift since the last
    /// reconciliation. Tagged by <see cref="ReconciliationConsumer"/> and <see cref="ProjectionKind"/>.
    /// The <c>projection-reconciliation</c> alert rule reads this by exactly this string; snake_case-
    /// with-unit-suffix (OTLP→Prometheus no-op), the <c>_total</c> the OTLP cumulative convention bakes
    /// into the emitted name — never a <c>babelstone.*</c> span key.
    /// </summary>
    public const string ReconciliationChecksumMismatchMetric = "reconciliation_checksum_mismatch_total";

    /// <summary>
    /// Projection-reconciliation EVENT-COUNT DRIFT / SKIP (event-store §7.1 pattern (b), M.5): a
    /// monotonic counter incremented when event-count reconciliation finds a consumer whose belief
    /// reflects FEWER folded events than truly exist at/below its claimed sequence
    /// (<c>EventCountStatus.Skip</c>) — it advanced past events it never applied (lost/dropped events).
    /// A benign <c>Gap</c> (acceptable async lag, §7.1) is deliberately NOT counted, so this series is a
    /// clean alertable skip signal. Tagged by <see cref="ReconciliationConsumer"/> and
    /// <see cref="ProjectionKind"/>. snake_case metric name read by the alert rule by this exact string.
    /// </summary>
    public const string ReconciliationEventCountDriftMetric = "reconciliation_event_count_drift_total";

    /// <summary>
    /// Projection-reconciliation REBUILD-DRILL DIVERGENCE (event-store §7.2 pattern (c), M.5): a
    /// monotonic counter incremented when a full-rebuild drill cold-re-folds the log and does NOT
    /// reproduce the running projection byte-for-byte (<c>RebuildReconciliation.Identical == false</c>)
    /// — the slow-drift bug class the cheap daily checksum can miss. The in-process companion to the
    /// <see cref="ReconciliationDrillFreshnessMetric"/> gauge: freshness catches a drill that did not
    /// RUN; this catches a drill that RAN and FAILED. Tagged by <see cref="ProjectionKind"/>. snake_case
    /// metric name read by the alert rule by this exact string.
    /// </summary>
    public const string ReconciliationRebuildDrillDivergenceMetric = "reconciliation_rebuild_drill_divergence_total";

    /// <summary>
    /// Projection-rebuild-drill FRESHNESS gauge (event-store §7.2, M.5): an observable gauge of the
    /// Unix-epoch SECONDS of the most recent SUCCESSFUL in-process full-rebuild drill
    /// (<c>RebuildReconciliation.Identical == true</c>) the reconciler has observed this process. The
    /// <c>ProjectionRebuildDrillStale</c> alert fires when <c>time() − this &gt; 35 days</c> (the §7.2
    /// monthly cadence + grace), and <c>absent()</c> covers a never-recorded drill — a missed drill is a
    /// process incident (ADR-PC-005). It is the in-process companion to the externally-pushed
    /// drill-freshness metric the projection-rebuild-drill script emits via Pushgateway; both carry the
    /// SAME name so the rule reads either source uniformly. snake_case-with-unit-suffix, not a span key.
    /// </summary>
    public const string ReconciliationDrillFreshnessMetric = "reconciliation_drill_last_success_timestamp_seconds";

    /// <summary>
    /// The CONSUMER dimension the reconciliation counters are tagged with (the
    /// <c>ReconciliationContract.Consumer</c> stable name, e.g. <c>engine</c> / <c>acl</c> /
    /// <c>notification</c>) — the same identity used in the AsyncAPI <c>x-authorized-consumers</c> list.
    /// A structural REFERENCE, never PII (ADR-PC-004 / catalogue OBS_NO_PII_ATTRS). The metric label
    /// key is the bare <c>consumer</c> string the alert rules group by — it is a metric dimension, not a
    /// <c>babelstone.*</c> span-attribute key, so it does not carry the span-key prefix.
    /// </summary>
    public const string ReconciliationConsumer = "consumer";

    /// <summary>
    /// The PROJECTION-KIND dimension the reconciliation counters/gauge are tagged with (the
    /// family-prefixed discriminator, e.g. <c>term_deposit.deposit_position</c> — the same
    /// <c>IProjectionRunner.Kind</c> the runner uses). A structural reference, never PII. The metric
    /// label key is the bare <c>projection_kind</c> string the alert rules group by — a metric
    /// dimension, not a <c>babelstone.*</c> span-attribute key.
    /// </summary>
    public const string ProjectionKind = "projection_kind";

    /// <summary>
    /// SNAPSHOT LAG (ADR-PC-003 / event-store §8.1): an <i>observable
    /// gauge</i> of the largest un-snapshotted event count observed across streams since process start —
    /// the depth of events appended past a stream's latest snapshot. The post-commit snapshot path
    /// (<c>AggregateRuntime.TrySnapshotAsync</c>) updates the high-water mark each time it evaluates the
    /// per-N trigger; the gauge reports it each collection cycle. Gauge-shaped (a current depth, not a
    /// cumulative total) because the <c>SnapshotLagHigh</c> alert reads it instantaneously
    /// (<c>snapshot_lag_events &gt; 500</c>) — a counter, which only ever climbs, would never describe
    /// "how far behind is the snapshotter RIGHT NOW". It keeps reporting even when nothing snapshots, so
    /// the ADR-PC-003 WARNING fires during a snapshotter outage — the exact failure mode it exists to catch.
    /// Snapshots are a rebuildable cache, so this is a WARNING (a deep un-snapshotted stream makes the
    /// next cold replay slower, never wrong). The metric name is the alert-rule contract string — a
    /// Prometheus/Grafana query reads it by this exact name — so it is snake_case, never a
    /// <c>babelstone.*</c> span key.
    /// </summary>
    public const string SnapshotLagEventsMetric = "snapshot_lag_events";

    /// <summary>
    /// SNAPSHOT HASH-MISMATCH ON READ (ADR-PC-003 / event-store §8.3): a
    /// monotonic counter incremented where <c>SnapshotStore.Verify</c> finds a snapshot's stored
    /// <c>(state ‖ last_event_id)</c> hash disagreeing with a recompute on read — the worst
    /// event-sourcing failure mode (a silently-wrong snapshot trusted as truth), caught by the §8.3
    /// guard. The read still throws and falls back to a cold fold (the ADR-PC-003 correctness fallback), so a
    /// single mismatch is recoverable (discard-and-rebuild); a RECURRING one is a snapshot-infrastructure
    /// bug to page on, which is why the <c>SnapshotHashMismatch</c> alert reads
    /// <c>increase(snapshot_hash_mismatch_total[1h]) &gt; 0</c> at <c>severity: critical</c>. snake_case-
    /// with-unit-suffix (the <c>_total</c> the OTLP cumulative convention bakes into the emitted name),
    /// read by the alert rule by this exact string — never a <c>babelstone.*</c> span key.
    /// </summary>
    public const string SnapshotHashMismatchMetric = "snapshot_hash_mismatch_total";

    /// <summary>
    /// Lifecycle-driver DISPATCHES (bd babelstone-1nkm.4; ADR-PC-036 / ADR-PC-038): a monotonic counter
    /// incremented once per due occurrence the driver's schedule pass successfully POSTed to the engine's
    /// ADR-PC-029 command surface AND durably recorded on <c>lifecycle_dispatch_ledger</c> — the
    /// money-mover's basic throughput signal ("did today's maturities/installments fire?"). Tagged by
    /// <see cref="LifecycleCommandKindTag"/> only (a kind code, never PII). snake_case-with-unit-suffix
    /// (the <c>_total</c> the OTLP cumulative convention bakes into the emitted name), read by the
    /// <c>lifecycle-driver</c> alert group by this exact string — never a <c>babelstone.*</c> span key.
    /// </summary>
    public const string LifecycleDispatchedMetric = "lifecycle_dispatch_total";

    /// <summary>
    /// Lifecycle-driver DISPATCH FAILURES (bd babelstone-1nkm.4): a monotonic counter incremented when a
    /// claimed occurrence's POST throws (a non-2xx engine response, a timeout, a transport error) — the
    /// occurrence stays un-recorded and re-claimable, the worker backs off, and the engine's
    /// <c>command_dedup</c> keeps the eventual retry effectively-once, so ONE failure is routine
    /// backpressure; a SUSTAINED rate means the always-on money-mover cannot reach the engine and due
    /// money movement is stalling (the <c>LifecycleDispatchFailuresSustained</c> alert). Tagged by
    /// <see cref="LifecycleCommandKindTag"/>. snake_case metric name, read by the alert rule by this
    /// exact string.
    /// </summary>
    public const string LifecycleDispatchFailureMetric = "lifecycle_dispatch_failure_total";

    /// <summary>
    /// Lifecycle DISPATCH LAG histogram (bd babelstone-1nkm.4): the seconds between an occurrence's
    /// business due date (UTC midnight of <c>due_at</c>) and the moment the driver successfully
    /// dispatched it, recorded once per dispatch and tagged by <see cref="LifecycleCommandKindTag"/>.
    /// A poll-interval's worth of lag is by design (ADR-PC-036 tolerates a due date firing up to one
    /// interval late); DAYS of lag means the calendar is surfacing work the driver keeps failing to
    /// land, or a backfill after a long outage — the <c>LifecycleDispatchLagP99High</c> alert reads the
    /// p99. Deliberately a per-dispatch histogram, not the backlog-age-gauge shape of
    /// <see cref="OutboxPublishLagMetric"/>: the "nothing is dispatching at all" outage mode is covered
    /// by <see cref="LifecyclePassFreshnessMetric"/> going stale, which keeps reporting when this series
    /// goes silent. snake_case-with-unit-suffix, read by the alert rule by this exact string.
    /// </summary>
    public const string LifecycleDispatchLagMetric = "lifecycle_dispatch_lag_seconds";

    /// <summary>
    /// Lifecycle-driver TICK-LIVENESS gauge (bd babelstone-1nkm.4): an observable gauge of the
    /// Unix-epoch SECONDS of the most recent schedule pass that ran to COMPLETION (every registered
    /// family rule evaluated; every claimed occurrence either recorded or released). This is the
    /// always-on host's heartbeat — the health surface the alert rules read (the same
    /// freshness-plus-<c>absent()</c> posture as <see cref="ReconciliationDrillFreshnessMetric"/> and
    /// the <c>EngineMetricsAbsent</c> staging-liveness rule): <c>LifecycleDriverTickStale</c> fires when
    /// <c>time() − this</c> exceeds the poll interval with margin (the loop is wedged, crash-looping, or
    /// backing off against a dead dependency), and <c>absent()</c> covers a driver that never completed
    /// a pass. Emits nothing until the first completed pass. snake_case-with-unit-suffix, read by the
    /// alert rules by this exact string.
    /// </summary>
    public const string LifecyclePassFreshnessMetric = "lifecycle_pass_last_success_timestamp_seconds";

    /// <summary>
    /// Lifecycle SCHEDULE-HELD occurrences (bd babelstone-1nkm.4; ADR-PC-036 §Decision 4 / LCD-2): a
    /// monotonic counter for each recurring occurrence N+1 a family rule HOLDS because occurrence N's
    /// de-settled cash leg is parked in <c>HUMAN_INTERVENTION_REQUIRED</c> — the settlement-health gate.
    /// This stall is SILENT by construction (there is no arrears state for the miss to land in;
    /// ADR-PC-036 §Residual-risks says it "must be alerted, not invisible"), so the
    /// <c>LifecycleScheduleHeld</c> alert pages on any increase. The EMIT hook
    /// (<c>LifecycleDriverMetrics.RecordScheduleHeld</c>) ships with the monitoring surface; the gate
    /// that calls it is the LCD-2 build (bd babelstone-6cpq.10) — until it lands the series is absent
    /// and the alert is dormant by construction. Tagged by <see cref="LifecycleCommandKindTag"/>.
    /// snake_case metric name, read by the alert rule by this exact string.
    /// </summary>
    public const string LifecycleScheduleHeldMetric = "lifecycle_schedule_held_total";

    /// <summary>
    /// The COMMAND-KIND dimension the lifecycle-driver metrics are tagged with (the stable
    /// ADR-PC-036 §Decision 1 kind code, e.g. <c>pay_installment</c> / <c>mature_deposit</c> — the same
    /// value <see cref="LifecycleCommandKind"/> carries on spans). A structural reference, never PII
    /// (ADR-PC-004 / OBS_NO_PII_ATTRS). The metric label key is the bare <c>command_kind</c> string the
    /// alert rules group by — a metric dimension, not a <c>babelstone.*</c> span-attribute key, so it
    /// does not carry the span-key prefix (the same register split as
    /// <see cref="ReconciliationConsumer"/> / <see cref="ProjectionKind"/>).
    /// </summary>
    public const string LifecycleCommandKindTag = "command_kind";

    /// <summary>
    /// Webhook delivery-attempt outcomes (ADR-IC-011): a monotonic counter incremented once per
    /// delivery attempt the notification drain pass classifies, tagged by
    /// <see cref="NotificationDeliveryOutcomeTag"/> (<c>delivered</c> / <c>transient_retry</c> /
    /// <c>abandoned</c> / <c>dead_lettered</c>). The rates an operator alarms on — a rising
    /// <c>abandoned</c> is a misconfigured receiver endpoint, a rising <c>dead_lettered</c> is
    /// exhaustion — where a log line alone is not aggregable. snake_case, read by dashboards/alerts by
    /// this exact string.
    /// </summary>
    public const string NotificationDeliveriesMetric = "notification_deliveries_total";

    /// <summary>
    /// The OUTCOME dimension of <see cref="NotificationDeliveriesMetric"/> — the delivery pass's
    /// classification of one attempt (ADR-IC-011 status handling): <c>delivered</c>,
    /// <c>transient_retry</c>, <c>abandoned</c>, or <c>dead_lettered</c>. A closed structural
    /// vocabulary, never PII (ADR-PC-004 / OBS_NO_PII_ATTRS). Carries the <c>babelstone.*</c> prefix —
    /// the same tagging register as the outbox publish-latency histogram's
    /// <see cref="AggregateType"/> tag.
    /// </summary>
    public const string NotificationDeliveryOutcomeTag = "babelstone.delivery_outcome";

    /// <summary>
    /// The exhausted-announcement backlog-age gauge (ADR-IC-004 posture, the notification estate's
    /// mirror of <see cref="OutboxPublishLagMetric"/>): the age in seconds of the OLDEST
    /// <c>PENDING</c> <c>notification_delivery_exhausted</c> outbox row at each collection cycle,
    /// computed single-clock in the DB (0 when empty). A per-published counter goes silent exactly
    /// when the relay is wedged or the broker/registry is unreachable — this gauge keeps climbing, so
    /// it is the alert that catches a delivery estate silently accumulating unannounced dead-letters
    /// (including the configured-store-but-no-backbone mode, which the host also WARNs about at boot).
    /// snake_case-with-unit-suffix, read by alert rules by this exact string.
    /// </summary>
    public const string NotificationExhaustedPendingLagMetric =
        "notification_delivery_exhausted_pending_lag_seconds";

    /// <summary>
    /// <c>NotificationDeliveryExhausted</c> events published to the backbone (ADR-IC-011): a monotonic
    /// counter incremented once per broker-acked relay publish. The throughput face of the exhaustion
    /// relay; its stall face is <see cref="NotificationExhaustedPendingLagMetric"/>. snake_case, read
    /// by dashboards by this exact string.
    /// </summary>
    public const string NotificationExhaustedPublishedMetric =
        "notification_delivery_exhausted_published_total";

    /// <summary>
    /// Payout-landing RECONCILIATION SIGNALS (bd babelstone-qa92.2; ADR-PC-043): a monotonic counter
    /// incremented once per NON-matched <c>ReconciliationSignal</c> the scheduled payout-landing reconciler
    /// pass surfaces — the operational face of "did a payout drop, double, or land at the wrong amount?".
    /// The reconciler classifies each source payout against its CA landing and this counts every case that
    /// needs a human (Drop / Double / WrongAmount / OrphanLanding); a Matched pair increments nothing and an
    /// in-SLA InFlight is not yet signalled. Tagged by <see cref="PayoutReconciliationClassTag"/> (the closed
    /// <c>ReconciliationClass</c> code — a structural verdict, never PII, ADR-PC-004 / OBS_NO_PII_ATTRS), so
    /// the <c>payout-landing-reconciliation</c> alert group can fire per class. snake_case-with-unit-suffix
    /// (the <c>_total</c> the OTLP cumulative convention bakes into the emitted name), read by the alert rules
    /// by this exact string — never a <c>babelstone.*</c> span key. The reconciler NEVER moves money: it
    /// SURFACES the fact (ADR-PC-043 reconcile-signals-only), and this counter is that surface.
    /// </summary>
    public const string PayoutReconciliationSignalMetric = "payout_reconciliation_signal_total";

    /// <summary>
    /// Payout-reconciliation TICK-LIVENESS gauge (bd babelstone-qa92.2): an observable gauge of the
    /// Unix-epoch SECONDS of the most recent payout-landing reconciliation pass that ran to COMPLETION. This
    /// is the scheduled reconciler's heartbeat — the safety-net's own health surface, the same
    /// freshness-plus-<c>absent()</c> posture as <see cref="LifecyclePassFreshnessMetric"/> and the
    /// <c>EngineMetricsAbsent</c> staging-liveness rule: a reconciler that stops ticking would let a DROP go
    /// unnoticed, so <c>time() − this</c> going stale (or <c>absent()</c> before the first pass) is itself an
    /// alert. Emits nothing until the first completed pass. snake_case-with-unit-suffix, read by the alert
    /// rules by this exact string.
    /// </summary>
    public const string PayoutReconciliationPassFreshnessMetric =
        "payout_reconciliation_pass_last_success_timestamp_seconds";

    /// <summary>
    /// The RECONCILIATION-CLASS dimension the payout-reconciliation signal counter is tagged with — the
    /// closed <c>ReconciliationClass</c> verdict code (<c>drop</c> / <c>double</c> / <c>wrong_amount</c> /
    /// <c>orphan_landing</c> / <c>in_flight</c> / <c>matched</c>). A structural reference, never PII
    /// (ADR-PC-004 / OBS_NO_PII_ATTRS). The metric label key is the bare <c>reconciliation_class</c> string
    /// the alert rules group by — a metric dimension, not a <c>babelstone.*</c> span-attribute key, so it
    /// does not carry the span-key prefix (the same register split as <see cref="LifecycleCommandKindTag"/>
    /// and <see cref="ProjectionKind"/>).
    /// </summary>
    public const string PayoutReconciliationClassTag = "reconciliation_class";
}
