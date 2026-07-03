using System.Diagnostics;
using System.Diagnostics.Metrics;
using Babelstone.Telemetry;
using OpenTelemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace Babelstone.Telemetry.Hosting;

/// <summary>
/// The RUNTIME, emit-time no-PII guard for OpenTelemetry signals (commitment <c>OBS_NO_PII_ATTRS</c> /
/// catalogue row OBS-3; ADR-IC-007; ADR-PC-004). In plain terms: the build-time analyser
/// (BENG005) can only catch a PII key written as a literal at a call site, but every REAL span/log
/// attribute and metric dimension in babelstone carries a value computed at runtime (an id's
/// <c>.ToString()</c>, a money-cents <c>long</c>, a topic name), so the analyser fires on none of them.
/// This guard is the control that actually keeps personal data out of the regulated trace/log/metric
/// store: it inspects every attribute AS IT IS EMITTED and strips anything outside the admitted tier.
/// It is the load-bearing leg of OBS-3 (the analyser is a secondary build-time tripwire).
///
/// <para>
/// <b>Three signals, one allowlist.</b> A span processor sees traces, a log processor sees log records,
/// and a metric View sees dimensions — three disjoint OTel surfaces that no single hook spans, so the
/// guard offers three overloads of <see cref="AddBabelstonePiiGuard(TracerProviderBuilder)"/> that
/// share one tier definition (the <see cref="AdmittedKeyPrefixes"/> namespace allowlist + the
/// <see cref="PiiKeyFragments"/> denylist — the same fragment set the BENG005 analyser and the
/// <c>TelemetrySpanTests</c> runtime assertion scan, so all three controls agree on what "PII" means).
/// </para>
///
/// <para>
/// <b>Why traces and logs enforce the tier differently.</b> A manual span tag is always a NAMESPACED key
/// — the <c>babelstone.*</c> contract (<see cref="BabelstoneAttributes"/>) or an OTel semantic-convention
/// namespace the auto-instrumentation emits (<c>db.*</c>, <c>http.*</c>, <c>server.*</c>, …) — so the
/// trace processor is a strict, fail-CLOSED prefix allowlist: a tag whose key is outside
/// <see cref="AdmittedKeyPrefixes"/> is dropped, full stop. A structured-LOG field, by contrast, is
/// conventionally a bare message-template name (<c>Account</c>, <c>AmountCents</c>, <c>AggregateId</c>,
/// and the ADR-IC-007 trio <c>correlation_id</c>/<c>process_id</c>/<c>deposit_id</c>) — NONE of which is
/// namespaced. A strict prefix allowlist would strip every structured log field, including the ADR-IC-007
/// references that are explicitly sufficient and non-PII; so the log processor keeps an un-namespaced
/// key UNLESS it carries a PII fragment (<c>account</c>, <c>nif</c>, <c>iban</c>, …) — exactly the
/// analyser's <c>KeyIsPii</c> rule. Both processors admit the same namespaces; they differ only in how
/// they treat the un-namespaced remainder, and the difference is forced by what each signal legitimately
/// carries. Note a semantic-convention key like <c>server.address</c> contains the <c>address</c>
/// fragment yet is admitted by its <c>server.</c> prefix — the namespace allowlist is checked first,
/// which is why a fragment denylist alone would wrongly blind Tempo/Grafana.
/// </para>
///
/// <para>
/// <b>Corrective signal (not silently fail-open).</b> Every stripped attribute increments
/// <see cref="StrippedAttributes"/> (<c>telemetry_pii_attributes_stripped_total</c>), tagged by
/// <see cref="TelemetrySignalTagKey"/> (<c>trace</c>/<c>log</c>). In a conformant build this counter
/// stays at zero — every real call site is admitted — so a non-zero rate is the alertable signal that a
/// bad attribute pattern has shipped past code review and the analyser, surfacing it for a fix rather
/// than letting the strip pass unnoticed.
/// </para>
///
/// <para>
/// <b>Scope (a key envelope).</b> The guard classifies attribute KEYS; it does not inspect VALUES inside
/// an admitted namespace. The auto-instrumentation is configured PII-safe by default (Npgsql attaches the
/// operation shape, never <c>db.statement</c> text — see <see cref="BabelstoneNpgsqlInstrumentation"/>;
/// AspNetCore captures <c>url.path</c>, never <c>url.query</c> or request headers), so an admitted
/// namespace does not smuggle a value-side leak. Enabling value-bearing instrumentation options would be
/// a deliberate, reviewed decision at the registration call site, the same envelope the Npgsql seam keeps.
/// </para>
/// </summary>
public static class BabelstonePiiGuard
{
    /// <summary>
    /// The ordinal key-prefix namespace allowlist (ADR-IC-007 operational tier). Admits:
    /// <list type="bullet">
    ///   <item><c>babelstone.</c> — the versioned <see cref="BabelstoneAttributes"/> key contract
    ///   (operational-tier structural identifiers; the salted <c>babelstone.subject_pseudonym</c> lives
    ///   here too — ADR-IC-016 plane iii).</item>
    ///   <item>the OTel/Npgsql/AspNetCore semantic-convention namespaces the auto-instrumentation
    ///   legitimately emits — <c>db.*</c>, <c>http.*</c>, <c>url.*</c>, <c>server.*</c>, <c>network.*</c>,
    ///   <c>service.*</c>, plus <c>error.*</c>, <c>exception.*</c>, <c>otel.*</c>, <c>user_agent.*</c>,
    ///   <c>telemetry.*</c>, <c>code.*</c>. Admitting these wholesale is what keeps the auto-instrumentation
    ///   visible in Tempo/Grafana (a <c>babelstone.*</c>-only allowlist would blind the very debugging
    ///   ADR-IC-007 exists to serve). These tags carry the OPERATION shape, never a parameter value.</item>
    /// </list>
    /// Checked first, allocation-free (ordinal <see cref="string.StartsWith(string, StringComparison)"/>).
    /// </summary>
    private static readonly string[] AdmittedKeyPrefixes =
    [
        "babelstone.",
        "db.",
        "http.",
        "url.",
        "server.",
        "network.",
        "service.",
        "error.",
        "exception.",
        "otel.",
        "user_agent.",
        "telemetry.",
        "code.",
    ];

    /// <summary>
    /// Key fragments that mark a non-namespaced key as PII-bearing — the SAME set the BENG005
    /// <c>NoPiiTelemetryAttributeAnalyzer</c> and the <c>TelemetrySpanTests</c> structural assertion scan,
    /// so the build-time tripwire, the runtime guard, and the fitness test agree on what "PII" means. Used
    /// only by the LOG processor, to decide whether an un-namespaced structured-state key is operational
    /// (kept) or PII (stripped); the trace processor never reaches this set (a non-admitted span key is
    /// dropped outright).
    /// </summary>
    private static readonly string[] PiiKeyFragments =
        ["nif", "iban", "account", "name", "email", "client", "phone", "address", "tax_id"];

    /// <summary>
    /// The metric tag-dimension allowlist enforced by the <see cref="AddBabelstonePiiGuard(MeterProviderBuilder)"/>
    /// View. Unlike span/log keys, an OTel metric View's <see cref="MetricStreamConfiguration.TagKeys"/> is an
    /// EXACT-key allowlist (no prefix matching), so this enumerates every admitted dimension the
    /// <c>Babelstone.Engine</c> meter instruments actually carry: the inbox/outbox topic dimensions
    /// (<c>babelstone.source_topic</c>, <c>babelstone.aggregate_type</c>), the reconciliation references
    /// (<c>consumer</c>, <c>projection_kind</c>), the saga-dispatch <c>command_type</c>, and this guard's own
    /// <see cref="TelemetrySignalTagKey"/>. A dimension whose key is not here is dropped at emit — a PII-shaped
    /// metric label never reaches the metrics backend.
    /// </summary>
    private static readonly string[] AdmittedMetricTagKeys =
    [
        BabelstoneAttributes.SourceTopic,            // babelstone.source_topic  (inbox counters)
        BabelstoneAttributes.AggregateType,          // babelstone.aggregate_type (outbox latency histogram)
        BabelstoneAttributes.ReconciliationConsumer, // consumer        (reconciliation counters)
        BabelstoneAttributes.ProjectionKind,         // projection_kind (reconciliation counters / gauge)
        SagaDispatchCommandTypeTagKey,               // command_type    (saga dispatch counters)
        BabelstoneAttributes.LifecycleCommandKindTag, // command_kind   (lifecycle-driver counters/histogram)
        TelemetrySignalTagKey,                       // this guard's own strip-counter dimension
    ];

    /// <summary>The bare metric dimension the saga command dispatcher tags its counters with
    /// (<c>SagaCommandDispatchDrainer</c>; not a <c>babelstone.*</c> key, so enumerated as a literal).</summary>
    private const string SagaDispatchCommandTypeTagKey = "command_type";

    /// <summary>The low-cardinality dimension on <see cref="StrippedAttributes"/> naming which signal a
    /// strip came from (<c>trace</c> or <c>log</c>). A <c>babelstone.*</c> key so it is itself admitted by
    /// the metric View's allowlist.</summary>
    public const string TelemetrySignalTagKey = "babelstone.telemetry_signal";

    /// <summary>The metric name of the corrective strip counter (snake_case-with-<c>_total</c>, the OTLP
    /// cumulative convention — a Prometheus/Grafana query reads it by this exact string).</summary>
    public const string StrippedAttributesMetricName = "telemetry_pii_attributes_stripped_total";

    // The corrective signal: a monotonic counter on the SHARED Babelstone meter, incremented once per
    // stripped attribute. With no meter listener (e.g. the notification host wires no MeterProvider) Add
    // is a near-zero-cost no-op; where a MeterProvider is wired (engine, orchestrator) it exports through
    // the same OTLP pipe, under the View's allowlist (which admits TelemetrySignalTagKey).
    private static readonly Counter<long> StrippedAttributes =
        BabelstoneTelemetry.Meter.CreateCounter<long>(
            StrippedAttributesMetricName,
            description: "Telemetry attributes stripped at emit by the runtime no-PII guard because their key " +
                        "was outside the admitted babelstone.*/semantic-convention tier (OBS_NO_PII_ATTRS / " +
                        "ADR-IC-007 §P4). Should be zero in a conformant build; a non-zero rate flags a PII " +
                        "attribute pattern that shipped past code review and the BENG005 analyser.");

    private const string TraceSignal = "trace";
    private const string LogSignal = "log";

    /// <summary>
    /// Registers the runtime no-PII guard for TRACES on the host's EXISTING tracer provider. Call
    /// inside the same <c>WithTracing(...)</c> lambda that does <c>AddSource(...)</c>, immediately before
    /// <c>.AddOtlpExporter()</c>, so the guard's <c>OnEnd</c> strips any non-admitted span tag BEFORE the
    /// exporter serialises the span. Mirrors the <see cref="BabelstoneNpgsqlInstrumentation"/> seam shape.
    /// </summary>
    public static TracerProviderBuilder AddBabelstonePiiGuard(this TracerProviderBuilder tracing)
        => tracing.AddProcessor(new BabelstoneAttributeTierProcessor());

    /// <summary>
    /// Registers the runtime no-PII guard for LOGS on the host's OTel logger provider. Call inside
    /// the <c>WithLogging(...)</c> lambda, before <c>.AddOtlpExporter()</c>, so the guard strips any
    /// PII-fragment structured-state field BEFORE the exporter serialises the record.
    /// </summary>
    public static LoggerProviderBuilder AddBabelstonePiiGuard(this LoggerProviderBuilder logging)
        => logging.AddProcessor(new BabelstoneLogRecordTierProcessor());

    /// <summary>
    /// Registers the runtime no-PII guard for METRICS on the host's meter provider as an OTel
    /// <c>View</c> with an explicit <see cref="MetricStreamConfiguration.TagKeys"/> allowlist over the
    /// <c>Babelstone.Engine</c> meter instruments — the only emit-time metric filter (a processor cannot
    /// touch metrics). A dimension whose key is outside <see cref="AdmittedMetricTagKeys"/> is dropped at
    /// emit. Scoped to the Babelstone meter, so the Npgsql meter's <c>db.*</c> dimensions are untouched.
    /// </summary>
    public static MeterProviderBuilder AddBabelstonePiiGuard(this MeterProviderBuilder metrics)
        => metrics.AddView(instrument =>
            instrument.Meter.Name == BabelstoneTelemetry.MeterName
                ? new MetricStreamConfiguration { TagKeys = AdmittedMetricTagKeys }
                : null);

    /// <summary>True if <paramref name="key"/> is in an admitted namespace (<see cref="AdmittedKeyPrefixes"/>).
    /// Allocation-free ordinal prefix scan — the hot-path check on every span tag and log field.</summary>
    internal static bool IsAdmittedNamespace(string key)
    {
        foreach (var prefix in AdmittedKeyPrefixes)
        {
            if (key.StartsWith(prefix, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>True if an un-namespaced <paramref name="key"/> carries a PII fragment (the log-side
    /// denylist; mirrors the analyser's <c>KeyIsPii</c>). Lower-cased once, then an ordinal contains scan.</summary>
    internal static bool CarriesPiiFragment(string key)
    {
        var lowered = key.ToLowerInvariant();
        foreach (var fragment in PiiKeyFragments)
        {
            if (lowered.Contains(fragment, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Record <paramref name="count"/> stripped attributes for the given signal on the corrective counter.</summary>
    internal static void RecordStripped(string signal, int count)
        => StrippedAttributes.Add(count, new KeyValuePair<string, object?>(TelemetrySignalTagKey, signal));
}

/// <summary>
/// The TRACE leg of the runtime no-PII guard: a <see cref="BaseProcessor{Activity}"/> whose
/// <see cref="OnEnd"/> walks the ending span's tags and drops any whose key is outside the admitted
/// <c>babelstone.*</c>/semantic-convention namespace allowlist, BEFORE the span is exported. Fail-closed:
/// a manual span tag is always namespaced, so anything else (a stray un-namespaced or PII-named key) is
/// removed outright. Allocation-free on the hot path — the common all-admitted span allocates nothing
/// (one <c>foreach</c> over the struct tag enumerator); a strip list is materialised only when a tag
/// actually has to be removed, which a conformant build never hits.
/// </summary>
public sealed class BabelstoneAttributeTierProcessor : BaseProcessor<Activity>
{
    public override void OnEnd(Activity activity)
    {
        List<string>? toStrip = null;
        foreach (var tag in activity.TagObjects)
        {
            if (!BabelstonePiiGuard.IsAdmittedNamespace(tag.Key))
            {
                (toStrip ??= []).Add(tag.Key);
            }
        }

        if (toStrip is null)
        {
            return;
        }

        // Setting a tag to null removes it (System.Diagnostics.Activity contract). Done after enumeration
        // so we never mutate the tag collection we are iterating.
        foreach (var key in toStrip)
        {
            activity.SetTag(key, null);
        }

        BabelstonePiiGuard.RecordStripped("trace", toStrip.Count);
    }
}

/// <summary>
/// The LOG leg of the runtime no-PII guard: a <see cref="BaseProcessor{LogRecord}"/> whose
/// <see cref="OnEnd"/> filters the ending record's structured-state attributes, dropping any un-namespaced
/// key that carries a PII fragment (e.g. a <c>{Account}</c> message-template field) BEFORE the record is
/// exported, while keeping the admitted namespaces and the operational un-namespaced fields ADR-IC-007 relies on
/// (<c>correlation_id</c>/<c>process_id</c>/<c>deposit_id</c>). Allocation-free on the hot path — when no
/// field needs stripping the attribute list is left untouched.
/// </summary>
public sealed class BabelstoneLogRecordTierProcessor : BaseProcessor<LogRecord>
{
    public override void OnEnd(LogRecord logRecord)
    {
        var attributes = logRecord.Attributes;
        if (attributes is null || attributes.Count == 0)
        {
            return;
        }

        List<KeyValuePair<string, object?>>? kept = null;
        var stripped = 0;
        for (var i = 0; i < attributes.Count; i++)
        {
            var attribute = attributes[i];
            var admit = BabelstonePiiGuard.IsAdmittedNamespace(attribute.Key)
                        || !BabelstonePiiGuard.CarriesPiiFragment(attribute.Key);
            if (admit)
            {
                kept?.Add(attribute);
            }
            else
            {
                // First strip: materialise the kept-so-far prefix, then skip this PII-named field.
                if (kept is null)
                {
                    kept = new List<KeyValuePair<string, object?>>(attributes.Count);
                    for (var j = 0; j < i; j++)
                    {
                        kept.Add(attributes[j]);
                    }
                }

                stripped++;
            }
        }

        if (kept is null)
        {
            return;
        }

        logRecord.Attributes = kept;
        BabelstonePiiGuard.RecordStripped("log", stripped);
    }
}
