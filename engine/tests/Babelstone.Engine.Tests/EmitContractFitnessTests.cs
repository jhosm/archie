using System.Text.Json;
using System.Text.RegularExpressions;
using Xunit;

namespace Babelstone.Engine.Tests;

/// <summary>
/// Emit-path fitness functions for three load-bearing commitment-catalogue rows, in the same
/// PURE-REFLECTION / disk-scan idiom as <see cref="EngineFamilyAgnosticTests"/> — no containers,
/// no saga infrastructure, no family <c>ProjectReference</c> from this engine spine test project
/// (which would itself violate <c>ENGINE_FAMILY_AGNOSTIC</c>). Each test parses the family event
/// schemas / source and the family decider/service off disk and asserts the structural invariant.
///
/// The commitments proven here:
/// <list type="bullet">
/// <item><c>NO_CLOCK_DRIVEN_ENGINE_SIGNAL</c> (row 17, ADR-PC-023 slot 1) — no family event type
///   names a clock-driven "about-to-happen" signal, and no engine emit path is driven by a
///   clock/scheduler. Extends the <c>DETERMINISM_GATE</c> purity stance from the fold to the
///   emit path.</item>
/// <item><c>GL_POST_FLAG_NEVER_GATES</c> (row 5a, ADR-PC-012 slot 5) and
///   <c>NOTIFY_POST_FLAG_NEVER_GATES</c> (row 5b, ADR-PC-025 slot 5) — the family decide/append
///   path holds no GL/notify port and cannot be gated or unwound by a GL/notify outcome; emission
///   is post-commit, fire-and-forget through the outbox (delivery is DEF-2 deferred).</item>
/// <item><c>OBS_NO_PII_ATTRS</c>'s bus-surface sibling — no event envelope/payload schema field
///   carries PII (the repo's never-PII-on-the-durable-bus rule). Reuses the
///   <see cref="TelemetrySpanTests"/> PII-key-fragment detection over the emitted-event surface.</item>
/// </list>
/// </summary>
public sealed class EmitContractFitnessTests
{
    /// <summary>
    /// PII key fragments — the SAME structural detection <see cref="TelemetrySpanTests"/> applies to
    /// telemetry span tags, applied here to the event-payload field surface. A field whose name
    /// contains any of these would carry an identity attribute the durable bus must never see
    /// (cleartext OR ciphertext): per the repo PII rule and ADR-PC-012 / ADR-PC-025 Decision 1, the
    /// envelope carries references, never PII. Structural (keys, not values), so it stays robust.
    /// </summary>
    private static readonly string[] PiiKeyFragments =
        ["nif", "iban", "account", "name", "email", "client", "phone", "address", "tax_id", "customer", "depositor", "heir"];

    /// <summary>
    /// Clock-driven "about-to-happen" suffixes a family event type MUST NOT carry (ADR-PC-023 §D1):
    /// a signal whose only cause is "a date arrived" is not a fact about the aggregate and is not
    /// engine-emitted. These name the non-fact tense ("approaching", "upcoming", "imminent",
    /// "expiring", "pending", "reminder", "soon") the ADR's <c>DepositMaturityApproaching</c> /
    /// <c>PaymentDue</c> examples illustrate, broadened (per the replay-determinism review) toward
    /// the ADR's implied maturity/forecast vocabulary ("forecast", "warning", "alert", "window",
    /// "notice", "scheduled") so an off-the-original-list clock-driven name
    /// (<c>DepositMaturityForecast</c>) is also caught.
    ///
    /// IMPORTANT — this is a NAME HEURISTIC, not a semantic proof. It pattern-matches the terminal
    /// word of an event type and cannot prove a non-obviously-named type is fact-driven; the
    /// build-time analyser the ADR's "analyser + contract" gate column also names is the real
    /// completeness backstop (a separate, still-open layer — see the catalogue reconciliation note).
    /// The structural <see cref="Engine_emit_path_runs_no_clock_driven_scheduler"/> scheduler-scan is
    /// the orthogonal STRUCTURAL proof that no clock tick fires an emission; keep both.
    ///
    /// NOTE — the bare suffix "Due" is deliberately NOT in this list: it would collide with the
    /// canonical fact-driven event ADR-PC-025 names in its title, <c>NotificationDue</c> (caused by a
    /// domain event, NOT by a date arriving). The clock-driven "X is due because a DATE arrived"
    /// shape is instead matched ONLY via <see cref="ForbiddenClockDrivenCompounds"/> below
    /// (<c>PaymentDue</c> / <c>*MaturityDue</c>), so <c>NotificationDue</c> stays green. Do NOT
    /// re-add a bare "Due" suffix here (it would RED-flag the legitimate Accepted-ADR-PC-025 event).
    /// </summary>
    private static readonly string[] ForbiddenClockDrivenSuffixes =
    [
        "Approaching", "Upcoming", "Imminent", "Expiring", "Pending", "Reminder", "Soon",
        "Forecast", "Warning", "Alert", "Window", "Notice", "Scheduled",
    ];

    /// <summary>
    /// Clock-driven "X is due because a DATE arrived" shapes matched as COMPOUND whole-name forms
    /// (ADR-PC-023 §D1's <c>PaymentDue</c> example, plus the parallel <c>MaturityDue</c>) — never as
    /// a bare "Due" suffix. This anchoring on the compound is what lets the canonical fact-driven
    /// <c>NotificationDue</c> (ADR-PC-025) pass: it ends in "Due" but is neither <c>PaymentDue</c>
    /// nor a <c>*MaturityDue</c>, so it is not caught here.
    /// </summary>
    private static readonly string[] ForbiddenClockDrivenCompounds =
        ["PaymentDue", "MaturityDue"];

    /// <summary>
    /// The ONLY collaborator types the family decide/append path is sanctioned to inject — the
    /// positive allowlist behind <see cref="Family_decide_and_append_path_injects_no_gl_or_notify_port"/>.
    /// Each is here for a named reason that is NOT "synchronously gate the producing flow on a GL or
    /// notification outcome". A GL-posting or notification port (under ANY name) is deliberately
    /// absent: such an outcome must ride the post-commit outbox, never an injected synchronous call.
    /// <list type="bullet">
    /// <item><c>AggregateRuntime</c> — the engine append spine; emission rides its post-commit outbox.</item>
    /// <item><c>IRateSheetStore</c> — rate/config resolution, READ before the pure decide (no gate).</item>
    /// <item><c>ISettlementPort</c> — the money-movement leg (ADR-PC-016): a legitimate PRE-flag
    ///   debit/credit, a DISTINCT concern from a GL-posting or notification SIGNAL.</item>
    /// <item><c>VerifiedPack</c> — the pinned per-instance configuration (ADR-PC-009).</item>
    /// <item><c>EarlyTerminationPolicy</c> — a pure penalty-band policy value object, no I/O.</item>
    /// <item><c>string</c> — primitive bindings (the day-count / withholding primitive ids).</item>
    /// <item><c>IReadOnlyCollection</c> — the product's required commercial-eligibility preconditions
    ///   (ADR-PC-024 §1, from the config's <c>required_preconditions</c>): a pure, read-only config
    ///   value the decide path consumes synchronously to REFUSE before any debit — not a GL/notify
    ///   signal port. Same ADR-PC-009 walking-skeleton config stand-in shape as the day-count primitive
    ///   <c>string</c> and the <c>EarlyTerminationPolicy</c> value object above; no I/O, nothing to gate.</item>
    /// </list>
    /// </summary>
    private static readonly string[] AllowedDecideAppendDependencies =
    [
        "AggregateRuntime", "IRateSheetStore", "ISettlementPort", "VerifiedPack",
        "EarlyTerminationPolicy", "string", "IReadOnlyCollection",
    ];

    /// <summary>
    /// NO_CLOCK_DRIVEN_ENGINE_SIGNAL (row 17, ADR-PC-023 slot 1) — schema half: no family event
    /// SCHEMA declares a clock-driven "about-to-happen" event type. Scans every Avro event schema
    /// (<c>contracts/avro/**/*.avsc</c>) — the wire contract for the emitted-event surface — and
    /// fails if a record name carries a forbidden clock-driven suffix. A new
    /// <c>DepositMaturityApproaching.avsc</c> would fail here.
    /// </summary>
    [Fact]
    public void No_family_event_schema_declares_a_clock_driven_event_type()
    {
        var repoRoot = RepoRoot();
        var avroDir = Path.Combine(repoRoot, "contracts", "avro");
        Assert.True(Directory.Exists(avroDir), $"Avro schema directory not found on disk: {avroDir}");

        var schemas = Directory.EnumerateFiles(avroDir, "*.avsc", SearchOption.AllDirectories).ToList();
        Assert.NotEmpty(schemas);

        var violations = new List<string>();
        foreach (var schema in schemas)
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(schema));
            if (!doc.RootElement.TryGetProperty("name", out var nameElement))
            {
                continue;
            }

            var recordName = nameElement.GetString() ?? string.Empty;
            var forbidden = MatchedClockDrivenSuffix(recordName);
            if (forbidden is not null)
            {
                violations.Add($"{Path.GetFileName(schema)} declares '{recordName}' (clock-driven suffix '{forbidden}')");
            }
        }

        Assert.True(
            violations.Count == 0,
            "ADR-PC-023 §D1 (NO_CLOCK_DRIVEN_ENGINE_SIGNAL): no family event schema may declare a "
            + "clock-driven \"about-to-happen\" event type — a date arriving is not a fact about the "
            + "aggregate. Offending schemas:\n  " + string.Join("\n  ", violations));
    }

    /// <summary>
    /// NO_CLOCK_DRIVEN_ENGINE_SIGNAL (row 17, ADR-PC-023 slot 1) — CLR-type half: no family event
    /// RECORD type names a clock-driven signal. Scans the family event source
    /// (<c>families/**/Events.cs</c>) for the <c>public sealed record X(...) : DomainEvent</c>
    /// declarations and fails if any event type carries a forbidden clock-driven suffix. Belt-and-
    /// braces with the schema scan: the CLR type and the Avro record are two faces of the same
    /// emitted-event surface, and the determinism stance must hold on both.
    /// </summary>
    [Fact]
    public void No_family_event_type_names_a_clock_driven_signal()
    {
        var eventTypes = FamilyDomainEventTypeNames(RepoRoot());
        Assert.NotEmpty(eventTypes);

        // Non-vacuity guard: the regex must extract ALL current family DomainEvent types, not a
        // subset — 8 of these 11 have no .avsc (after the ADR-IC-017 §P4 promotion pass: InterestPaid
        // gained an .avsc; InterestAccrued + WithholdingApplied lost theirs, de-promoted to internal /
        // store-only), so a regex that silently dropped one would leave a schemaless event unguarded.
        // If a family adds/removes an event, update this count knowingly. (The TOTAL DomainEvent count
        // is unchanged — the §P4 change touches only WHICH of the 11 are catalogued, not Events.cs.)
        Assert.Equal(11, eventTypes.Count);

        var violations = eventTypes
            .Select(name => (name, suffix: MatchedClockDrivenSuffix(name)))
            .Where(x => x.suffix is not null)
            .Select(x => $"{x.name} (clock-driven suffix '{x.suffix}')")
            .ToList();

        Assert.True(
            violations.Count == 0,
            "ADR-PC-023 §D1 (NO_CLOCK_DRIVEN_ENGINE_SIGNAL): no family DomainEvent type may name a "
            + "clock-driven \"about-to-happen\" signal (no DepositMaturityApproaching / PaymentDue). "
            + "Every emitted signal traces to a causing domain event. Offending event types:\n  "
            + string.Join("\n  ", violations));
    }

    /// <summary>
    /// NO_CLOCK_DRIVEN_ENGINE_SIGNAL (row 17, ADR-PC-023 slot 1) — emit-path half: the engine runs
    /// NO internal scheduler/clock on the emit path (ADR-PC-023 §D1: "the engine runs no internal
    /// scheduler that emits a temporal signal"). The runtime stamps <c>transaction_time</c> from the
    /// injected <see cref="System.TimeProvider"/> at append (the impure shell owns the clock,
    /// ADR-PC-010 §P5), but it owns NO timer/scheduler that would FIRE an emission on a clock tick.
    /// Scans <see cref="Babelstone.Engine"/>'s spine source for the scheduler/timer primitives that
    /// would betray a clock-driven emitter (<c>System.Threading.Timer</c>, <c>PeriodicTimer</c>,
    /// <c>Task.Delay</c>, a hosted <c>BackgroundService</c>, <c>ITimer</c>) and fails if one appears
    /// on the spine — the structural guarantee no clock-tick fires an engine event.
    /// </summary>
    [Fact]
    public void Engine_emit_path_runs_no_clock_driven_scheduler()
    {
        var repoRoot = RepoRoot();
        var engineSrc = Path.Combine(repoRoot, "engine", "src", "Babelstone.Engine");
        Assert.True(Directory.Exists(engineSrc), $"engine spine source not found on disk: {engineSrc}");

        // Scheduler/timer primitives that, on the EMIT spine, would mean a clock tick — not a domain
        // event — produces a signal. CreateTimer is the TimeProvider factory for a recurring callback;
        // a bare GetUtcNow stamp (transaction_time) is allowed and deliberately NOT in this set.
        var schedulerPrimitives = new[]
        {
            "PeriodicTimer", "new Timer(", "Threading.Timer", "ITimer", "CreateTimer",
            "Task.Delay", "BackgroundService", "IHostedService", "Timer.Elapsed",
        };

        var violations = new List<string>();
        foreach (var file in Directory.EnumerateFiles(engineSrc, "*.cs", SearchOption.AllDirectories))
        {
            // Strip line comments first so a doc-comment or TODO naming a primitive (e.g.
            // "// deliberately no PeriodicTimer on the emit spine") cannot trip the scan — only the
            // executable surface betrays a clock-driven emitter (same discipline as the GL/notify scan).
            var code = StripLineComments(File.ReadAllText(file));
            foreach (var primitive in schedulerPrimitives)
            {
                // Word-boundary anchored so a primitive matches as a whole token, never as a substring
                // of a longer identifier — ITimer must not match ITimerFactory, Threading.Timer must
                // not match Threading.TimerQueue (a bare Contains would false-RED on both).
                if (Regex.IsMatch(code, SchedulerPrimitivePattern(primitive)))
                {
                    violations.Add($"{Path.GetFileName(file)} references scheduler primitive '{primitive}'");
                }
            }
        }

        Assert.True(
            violations.Count == 0,
            "ADR-PC-023 §D1 (NO_CLOCK_DRIVEN_ENGINE_SIGNAL): the engine emit spine runs no scheduler — "
            + "no clock tick may fire an engine-emitted signal (every signal traces to a causing domain "
            + "event). The runtime stamps transaction_time from the injected TimeProvider at append, but "
            + "owns no timer/scheduler. Offending references:\n  " + string.Join("\n  ", violations));
    }

    /// <summary>
    /// GL_POST_FLAG_NEVER_GATES (row 5a, ADR-PC-012 slot 5) + NOTIFY_POST_FLAG_NEVER_GATES (row 5b,
    /// ADR-PC-025 slot 5) — structural EMIT-side proof. The family decide/append path makes NO
    /// synchronous GL/notify call and cannot be gated or unwound by a GL/notify outcome: a GL reject
    /// or a notification failure happens strictly DOWNSTREAM of the engine's local commit (post-flag),
    /// reached only via the outbox the runtime writes in the same transaction as the event. Expressed
    /// structurally: the family decider + service source references NO GL/notify port symbol. The
    /// PRE_CONTRACTUAL (FIN) notification is the only synchronous-saga carve-out (ADR-PC-025 §"FIN is
    /// a saga step") and lives in the constitution saga, not the engine emit path; SCHEDULED is no
    /// longer engine-emitted (ADR-PC-023). Delivery itself is DEF-2 deferred — there is nothing to
    /// gate because nothing synchronous is wired.
    ///
    /// This is a CLOSED-WORLD ALLOWLIST, not a denylist: rather than enumerate the GL/notify port
    /// names a gate might use (a denylist any off-list name — <c>IGlSink</c>, <c>LedgerClient</c>,
    /// a generic <c>IOutboundPort&lt;T&gt;</c> — would slip past), it asserts that every collaborator
    /// INJECTED into the decide/append path is on the sanctioned set <see cref="AllowedDecideAppendDependencies"/>.
    /// A GL-posting or notification port — under ANY name — is by construction not on that set, so it
    /// fails this test the moment it is wired, regardless of what it is called. The append drivers are
    /// discovered structurally (any <c>Application</c> class whose primary constructor injects the
    /// <see cref="AggregateRuntime{TState}"/> append spine), and the pure <c>TermDepositDecider</c>
    /// is asserted <c>static</c> — it holds no field, so it cannot call an injected port at all.
    ///
    /// The authoritative gates remain the ADR-IC-009 Pact CDC contract tests (the GL/notify consumer
    /// contracts verified in the producer's CI; tracked: bd babelstone-2t16.14, gated on the DEF-2
    /// delivery ports bd babelstone-a7d4.2) and the runtime registry; this is the cheap structural
    /// tripwire that fails fast at PR time when a gating port is injected onto the producing flow.
    /// When the DEF-2 delivery ports land, the
    /// sanctioned post-flag emission surface they add is admitted by EXTENDING the allowlist with a
    /// one-line reason — a conscious edit, reviewed against "rides the post-commit outbox, never gates".
    /// </summary>
    [Fact]
    public void Family_decide_and_append_path_injects_no_gl_or_notify_port()
    {
        var repoRoot = RepoRoot();
        var familyAppDir = Path.Combine(
            repoRoot, "families", "term-deposit", "src",
            "Babelstone.Families.TermDeposit.Application");
        Assert.True(Directory.Exists(familyAppDir), $"family application source not found on disk: {familyAppDir}");

        // The pure decider is STATIC — it has no instance state, so it structurally cannot hold an
        // injected GL/notify port to call synchronously. A future refactor to a non-static decider
        // with injected collaborators must come back through this gate.
        var deciderPath = Path.Combine(familyAppDir, "TermDepositDecider.cs");
        Assert.True(File.Exists(deciderPath), $"decider not found on disk: {deciderPath}");
        Assert.Matches(
            @"\bstatic\s+class\s+TermDepositDecider\b",
            StripLineComments(File.ReadAllText(deciderPath)));

        // Every collaborator injected into an append driver (a class whose primary ctor takes the
        // AggregateRuntime append spine) must be on the sanctioned allowlist. A GL/notify port under
        // ANY name is not — it would ride the post-commit outbox, never a synchronous injected call.
        var injected = WritePathInjectedDependencies(familyAppDir);
        Assert.NotEmpty(injected); // non-vacuity: at least one append driver was actually parsed.

        var unsanctioned = injected
            .Where(d => !AllowedDecideAppendDependencies.Contains(d.Type, StringComparer.Ordinal))
            .Select(d => $"{d.Driver} injects '{d.Type}'")
            .ToList();

        Assert.True(
            unsanctioned.Count == 0,
            "ADR-PC-012 slot 5 (GL_POST_FLAG_NEVER_GATES) + ADR-PC-025 slot 5 (NOTIFY_POST_FLAG_NEVER_GATES): "
            + "the family decide/append path may inject ONLY the sanctioned post-flag collaborators "
            + "(AllowedDecideAppendDependencies). A GL-posting or notification port — under ANY name — must "
            + "NOT be injected here: such an outcome rides the post-commit outbox, never a synchronous call "
            + "that could gate or unwind the producing flow. The PRE_CONTRACTUAL FIN gate is the saga "
            + "carve-out, not on this path. If an unsanctioned dependency below is a legitimate non-GL/notify "
            + "collaborator, add it to the allowlist with a one-line reason; if it is a GL/notify outcome, "
            + "route it through the outbox instead. Unsanctioned injections:\n  " + string.Join("\n  ", unsanctioned));
    }

    /// <summary>
    /// GL_POST_FLAG_NEVER_GATES + NOTIFY_POST_FLAG_NEVER_GATES — positive half: emission goes through
    /// the OUTBOX. The append path the family service drives (<see cref="AggregateRuntime{TState}"/>)
    /// writes the event row AND its outbox row in one transaction (ADR-PC-001 §P2 / ES_ATOMIC_APPEND_OUTBOX),
    /// and the outbox is the ONLY emission channel — there is no synchronous publish on the write path.
    /// Asserts structurally that <see cref="AggregateRuntime{TState}.AppendAsync"/> builds an
    /// <c>OutboxRow</c> per event and commits via the sink, so every emitted signal rides the
    /// post-commit, fire-and-forget outbox rather than a gating synchronous call.
    ///
    /// The inline-publish half is a DENYLIST heuristic over a KNOWN SET of broker/publish shapes
    /// (<c>IProducer</c>, <c>ProduceAsync</c>, <c>kafka</c>, <c>HttpClient</c>, …) — it proves the
    /// absence of those named shapes on the write path, not a closed-world proof. The authoritative
    /// gates remain the ADR-IC-009 Pact CDC contract tests and the runtime registry; this is the
    /// cheap structural tripwire that fails fast if a known synchronous publish creeps inline.
    /// </summary>
    [Fact]
    public void Emission_goes_through_the_outbox_not_a_synchronous_publish()
    {
        var repoRoot = RepoRoot();
        var runtimePath = Path.Combine(
            repoRoot, "engine", "src", "Babelstone.Engine", "AggregateRuntime.cs");
        Assert.True(File.Exists(runtimePath), $"AggregateRuntime not found on disk: {runtimePath}");

        var runtime = File.ReadAllText(runtimePath);

        // The append path builds an outbox row per event and commits events + outbox via the sink in
        // one transaction — the outbox IS the emission channel (post-commit, fire-and-forget).
        Assert.Contains("new OutboxRow(", runtime, StringComparison.Ordinal);
        Assert.Contains("sink.AppendAsync(", runtime, StringComparison.Ordinal);

        // And the write path holds no synchronous outbound publish/dispatch to a broker that would
        // gate the commit — emission is the relay's job downstream of the outbox, never inline here.
        var inlinePublishPrimitives = new[]
        {
            "IProducer", "ProduceAsync", "PublishAsync", "kafka", "Kafka", "Redpanda", "HttpClient",
        };
        var violations = inlinePublishPrimitives
            .Where(p => runtime.Contains(p, StringComparison.Ordinal))
            .Select(p => $"AggregateRuntime references inline-publish primitive '{p}'")
            .ToList();

        Assert.True(
            violations.Count == 0,
            "ADR-PC-012/ADR-PC-025 slot 5: the append path must emit only through the outbox (no inline "
            + "synchronous broker publish on the write path), so a downstream consumer outcome can never "
            + "gate the commit. Offending references:\n  " + string.Join("\n  ", violations));
    }

    /// <summary>
    /// NO-PII-ON-BUS — the emitted-event surface (the durable-bus contract) carries no PII. Scans
    /// every Avro event schema (<c>contracts/avro/**/*.avsc</c>) field name with the SAME structural
    /// PII-key detection <see cref="TelemetrySpanTests"/> applies to span tags, and fails if any field
    /// name reads as an identity attribute (NIF/IBAN/account/name/email/customer/heir/…). The repo
    /// rule is absolute: never put PII — cleartext OR ciphertext — on the durable bus; carry references
    /// (ADR-PC-012 / ADR-PC-025 Decision 1: the envelope carries no PII; an opaque <c>*_ref</c> /
    /// <c>customer_id</c> reference is resolved internally, never the identity itself).
    /// </summary>
    [Fact]
    public void No_event_schema_field_carries_pii()
    {
        var repoRoot = RepoRoot();
        var avroDir = Path.Combine(repoRoot, "contracts", "avro");
        Assert.True(Directory.Exists(avroDir), $"Avro schema directory not found on disk: {avroDir}");

        var schemas = Directory.EnumerateFiles(avroDir, "*.avsc", SearchOption.AllDirectories).ToList();
        Assert.NotEmpty(schemas);

        var violations = new List<string>();
        foreach (var schema in schemas)
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(schema));
            var recordName = doc.RootElement.TryGetProperty("name", out var n) ? n.GetString() : Path.GetFileName(schema);
            if (!doc.RootElement.TryGetProperty("fields", out var fields) || fields.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var field in fields.EnumerateArray())
            {
                if (!field.TryGetProperty("name", out var fieldName))
                {
                    continue;
                }

                var lowered = (fieldName.GetString() ?? string.Empty).ToLowerInvariant();
                var fragment = PiiKeyFragments.FirstOrDefault(f => FieldNameCarriesPii(lowered, f));
                if (fragment is not null)
                {
                    violations.Add($"{recordName}.{fieldName.GetString()} (PII fragment '{fragment}')");
                }
            }
        }

        Assert.True(
            violations.Count == 0,
            "Never-PII-on-the-durable-bus (ADR-PC-012 / ADR-PC-025 Decision 1): no emitted-event schema "
            + "field may carry PII — cleartext OR ciphertext. The envelope carries opaque references "
            + "(customer_id, *_ref) resolved internally, never the identity. Offending fields:\n  "
            + string.Join("\n  ", violations));
    }

    /// <summary>
    /// NO-PII-ON-BUS — CLR half. The <c>.avsc</c> scan above covers only the 3 schema-backed events
    /// (the ADR-IC-017 §P4 promoted set: <c>DepositConstituted</c>, <c>InterestPaid</c>,
    /// <c>DepositMatured</c>); 8 of the 11 family events are schemaless today — including the most
    /// PII-adjacent (<c>DepositTransferredToHeirs</c>'s <c>HeirCaseRef</c>, <c>DepositCorrected</c>'s
    /// <c>PreviousValueRef</c>/<c>CorrectedValueRef</c>), the de-promoted accrual mechanics
    /// (<c>InterestAccrued</c>, <c>WithholdingApplied</c>), and the highest-risk future
    /// <c>NotificationDue</c> — so without this they ride to the bus unguarded until their
    /// <c>.avsc</c> exists. This scans the CLR record CONSTRUCTOR PARAMETER names off
    /// <c>families/**/Events.cs</c> (the same disk-scan idiom as <see cref="FamilyDomainEventTypeNames"/>),
    /// normalises each PascalCase parameter to the snake form the Avro fields use, and applies the
    /// SAME <see cref="FieldNameCarriesPii"/> detection + <c>_ref</c>/<c>_id</c> opaque-reference
    /// exclusion — so a future PII-bearing event field is caught at its CLR declaration, before any
    /// schema is written. <c>HeirCaseRef</c> / <c>*ValueRef</c> are opaque references and stay green.
    /// </summary>
    [Fact]
    public void No_family_event_clr_field_carries_pii()
    {
        var fields = FamilyDomainEventConstructorParameterNames(RepoRoot());
        Assert.NotEmpty(fields);

        var violations = new List<string>();
        foreach (var (eventType, parameter) in fields)
        {
            // Normalise PascalCase -> snake_case so HeirCaseRef reads as heir_case_ref and the SAME
            // _ref/_id opaque-reference exclusion the Avro-field scan uses applies unchanged.
            var lowered = PascalToSnake(parameter);
            var fragment = PiiKeyFragments.FirstOrDefault(f => FieldNameCarriesPii(lowered, f));
            if (fragment is not null)
            {
                violations.Add($"{eventType}.{parameter} (PII fragment '{fragment}')");
            }
        }

        Assert.True(
            violations.Count == 0,
            "Never-PII-on-the-durable-bus (ADR-PC-012 / ADR-PC-025 Decision 1) — CLR half: no family "
            + "DomainEvent record parameter may carry PII — cleartext OR ciphertext — even before its "
            + ".avsc exists. The event carries opaque references (HeirCaseRef, *ValueRef, *Id) resolved "
            + "internally, never the identity. Offending parameters:\n  " + string.Join("\n  ", violations));
    }

    /// <summary>
    /// A PII fragment match that excludes the structural false positives in this contract: a
    /// <c>*_ref</c> reference (e.g. <c>heir_case_ref</c>) is an OPAQUE handle the engine resolves
    /// internally — by design NOT PII — and <c>tax_cents</c> / <c>tax_basis_points</c> is a money/rate
    /// amount, not the <c>tax_id</c> identifier the fragment guards against (the tax/identifier
    /// disambiguation is why the fragment list carries the full <c>"tax_id"</c> token, not a bare
    /// <c>"tax"</c> — a tax amount/rate never contains the <c>_id</c> token). The fragment test still
    /// catches a genuine identity field (<c>customer_name</c>, <c>iban</c>, <c>nif</c>).
    /// </summary>
    private static bool FieldNameCarriesPii(string loweredFieldName, string fragment)
    {
        if (!loweredFieldName.Contains(fragment, StringComparison.Ordinal))
        {
            return false;
        }

        // An opaque reference handle ending in _ref (or named *_ref) is the carry-a-reference pattern,
        // not PII (ADR-PC-025 PII-by-reference). e.g. heir_case_ref, subject_ref.
        if (loweredFieldName.EndsWith("_ref", StringComparison.Ordinal)
            || loweredFieldName.EndsWith("_id", StringComparison.Ordinal))
        {
            return false;
        }

        return true;
    }

    /// <summary>
    /// The forbidden clock-driven token an event type name matches, or null if none. Matches a
    /// suffix from <see cref="ForbiddenClockDrivenSuffixes"/> (terminal word, e.g. <c>...Approaching</c>,
    /// <c>...Forecast</c>) OR a compound whole-name "due-on-a-date" form from
    /// <see cref="ForbiddenClockDrivenCompounds"/> (<c>PaymentDue</c> / <c>*MaturityDue</c>). The
    /// bare "Due" is matched ONLY via the compounds, so the fact-driven <c>NotificationDue</c>
    /// (ADR-PC-025) does NOT trip.
    /// </summary>
    private static string? MatchedClockDrivenSuffix(string eventTypeName)
        => ForbiddenClockDrivenSuffixes.FirstOrDefault(
               suffix => eventTypeName.EndsWith(suffix, StringComparison.Ordinal))
           ?? ForbiddenClockDrivenCompounds.FirstOrDefault(
               compound => eventTypeName.EndsWith(compound, StringComparison.Ordinal));

    /// <summary>
    /// The family <see cref="DomainEvent"/> type names declared in <c>families/**/Events.cs</c>, read
    /// off disk the same way <see cref="EngineFamilyAgnosticTests"/> reads the spine off its csproj —
    /// no family ProjectReference from this engine-spine test project (that would itself break
    /// ENGINE_FAMILY_AGNOSTIC). 8 of the 11 current family events have no <c>.avsc</c> (ADR-IC-017 §P4
    /// promoted set: <c>DepositConstituted</c>, <c>InterestPaid</c>, <c>DepositMatured</c>), so this scan
    /// is their ONLY naming guard — the regex must tolerate every record shape that lands a
    /// <c>: ... DomainEvent</c> base, not just the primary-ctor one:
    /// <list type="bullet">
    /// <item><c>record X(...) : DomainEvent</c> — positional / primary-ctor form (the 11 today)</item>
    /// <item><c>record class X(...) : DomainEvent</c> — explicit <c>class</c> kind</item>
    /// <item><c>record X : DomainEvent { }</c> — body form with NO primary-ctor parens</item>
    /// </list>
    /// So it anchors on <c>record (class )?Name</c> and a <c>: ... DomainEvent</c> base WITHOUT
    /// requiring parens, stopping the base scan at the first <c>{</c> or <c>;</c> so it never bleeds
    /// past the declaration. (The 3 schema-backed events are double-guarded by the <c>.avsc</c> scan.)
    /// </summary>
    private static IReadOnlyList<string> FamilyDomainEventTypeNames(string repoRoot)
    {
        var familiesDir = Path.Combine(repoRoot, "families");
        Assert.True(Directory.Exists(familiesDir), $"families directory not found on disk: {familiesDir}");

        var names = new List<string>();
        foreach (var eventsFile in Directory.EnumerateFiles(familiesDir, "Events.cs", SearchOption.AllDirectories))
        {
            // Skip generated / build output, mirroring EngineFamilyAgnosticTests' on-disk discipline.
            if (eventsFile.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                || eventsFile.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            {
                continue;
            }

            var source = File.ReadAllText(eventsFile);
            // record (class )?Name <anything-but-{-or-;> : <anything-but-{-or-;> DomainEvent — matches
            // positional, `record class`, and body-form declarations alike (no required parens).
            foreach (Match match in Regex.Matches(
                source, @"record\s+(?:class\s+)?([A-Z]\w*)\b[^{;]*:\s*[^{;]*\bDomainEvent\b", RegexOptions.Singleline))
            {
                names.Add(match.Groups[1].Value);
            }
        }

        return names;
    }

    /// <summary>
    /// The (event type, constructor parameter name) pairs declared in <c>families/**/Events.cs</c>,
    /// read off disk the same way <see cref="FamilyDomainEventTypeNames"/> reads the type names — no
    /// family ProjectReference. For each <c>record Name(...) : DomainEvent</c> it captures the
    /// parameter block and yields each parameter NAME (the identifier immediately before a <c>,</c>,
    /// <c>)</c>, or a <c>=</c> default), skipping the type tokens. Drives the CLR-field PII scan so a
    /// PII-bearing parameter on a SCHEMALESS event is caught before its <c>.avsc</c> exists.
    /// </summary>
    private static IReadOnlyList<(string EventType, string Parameter)> FamilyDomainEventConstructorParameterNames(string repoRoot)
    {
        var familiesDir = Path.Combine(repoRoot, "families");
        Assert.True(Directory.Exists(familiesDir), $"families directory not found on disk: {familiesDir}");

        var fields = new List<(string, string)>();
        foreach (var eventsFile in Directory.EnumerateFiles(familiesDir, "Events.cs", SearchOption.AllDirectories))
        {
            if (eventsFile.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                || eventsFile.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            {
                continue;
            }

            var source = File.ReadAllText(eventsFile);
            // record (class )?Name ( <param-block> ) : ... DomainEvent — capture the parameter block.
            foreach (Match record in Regex.Matches(
                source, @"record\s+(?:class\s+)?([A-Z]\w*)\s*\(([^)]*)\)\s*:\s*[^{;]*\bDomainEvent\b", RegexOptions.Singleline))
            {
                var eventType = record.Groups[1].Value;
                var paramBlock = record.Groups[2].Value;
                // A parameter NAME is the identifier immediately before a separator (',' or end of
                // block), allowing an optional '= default'. The type tokens never sit before a
                // separator, so only the parameter name is captured.
                foreach (Match param in Regex.Matches(
                    paramBlock, @"\b([A-Za-z_]\w*)\s*(?:=[^,]*)?\s*(?:,|$)", RegexOptions.Singleline))
                {
                    fields.Add((eventType, param.Groups[1].Value));
                }
            }
        }

        return fields;
    }

    /// <summary>
    /// Converts a PascalCase CLR parameter name to the lowercased snake_case shape the Avro fields
    /// use (<c>HeirCaseRef</c> → <c>heir_case_ref</c>), so the SAME <see cref="FieldNameCarriesPii"/>
    /// detection + <c>_ref</c>/<c>_id</c> opaque-reference exclusion applies to both surfaces.
    /// </summary>
    private static string PascalToSnake(string pascal)
        => Regex.Replace(pascal, @"(?<=[a-z0-9])(?=[A-Z])", "_").ToLowerInvariant();

    /// <summary>Strips C# line comments so prose in comments cannot match an executable-symbol scan.</summary>
    private static string StripLineComments(string source)
        => Regex.Replace(source, @"//.*?$", string.Empty, RegexOptions.Multiline);

    /// <summary>
    /// Builds a word-boundary-anchored regex for a scheduler primitive so it matches the whole token,
    /// not a substring of a longer identifier — <c>ITimer</c> must not match <c>ITimerFactory</c>,
    /// <c>Threading.Timer</c> must not match <c>Threading.TimerQueue</c>. A leading <c>\b</c> is added
    /// when the primitive starts with a word char and a trailing <c>\b</c> when it ends with one; a
    /// primitive ending in a non-word char (<c>new Timer(</c>) gets no trailing boundary (the
    /// <c>(</c> already anchors it, and a trailing <c>\b</c> would break the empty-arg <c>new Timer()</c>).
    /// </summary>
    private static string SchedulerPrimitivePattern(string primitive)
    {
        static bool IsWord(char c) => char.IsLetterOrDigit(c) || c == '_';
        var prefix = IsWord(primitive[0]) ? @"\b" : string.Empty;
        var suffix = IsWord(primitive[^1]) ? @"\b" : string.Empty;
        return prefix + Regex.Escape(primitive) + suffix;
    }

    /// <summary>
    /// The (driver, injected-dependency-type) pairs for every APPEND DRIVER on the family decide/append
    /// path, read off disk the same way the event scans read <c>Events.cs</c> — no family ProjectReference.
    /// An append driver is any class in the family <c>Application</c> source whose PRIMARY CONSTRUCTOR
    /// injects the <see cref="AggregateRuntime{TState}"/> append spine (the read-model store, which takes
    /// only a connection string, is correctly NOT one); discovering drivers structurally rather than by a
    /// hard-coded class name auto-covers a future <c>ConstitutionPipeline</c> / second-family service.
    /// For each driver it yields each ctor parameter's TYPE token (the leading token of the parameter,
    /// with generic args and trailing nullability stripped — <c>AggregateRuntime&lt;DepositPosition&gt;</c>
    /// → <c>AggregateRuntime</c>, <c>EarlyTerminationPolicy?</c> → <c>EarlyTerminationPolicy</c>) so the
    /// caller can assert the injected surface ⊆ <see cref="AllowedDecideAppendDependencies"/>. Keyed to
    /// the codebase's primary-constructor DI idiom (a stable house style); a driver written with a classic
    /// constructor body would not be matched and must be added knowingly.
    /// </summary>
    private static IReadOnlyList<(string Driver, string Type)> WritePathInjectedDependencies(string appDir)
    {
        var deps = new List<(string, string)>();
        foreach (var file in Directory.EnumerateFiles(appDir, "*.cs", SearchOption.AllDirectories))
        {
            var source = StripLineComments(File.ReadAllText(file));
            // `... class Name(<param-block>) :|{` — the primary-ctor param block has no nested parens,
            // so the lazy capture stops at the ctor's own ')'. Body/base follows as ':' or '{'.
            foreach (Match cls in Regex.Matches(
                source, @"class\s+([A-Z]\w*)\s*\(([^)]*)\)\s*(?::|\{)", RegexOptions.Singleline))
            {
                var paramBlock = cls.Groups[2].Value;
                // Only classes that inject the append spine are decide/append drivers — skip the rest
                // (read-model stores, value objects) so the allowlist stays scoped to the producing flow.
                if (!Regex.IsMatch(paramBlock, @"\bAggregateRuntime\b"))
                {
                    continue;
                }

                var driver = cls.Groups[1].Value;
                foreach (var rawParam in paramBlock.Split(','))
                {
                    var param = rawParam.Trim();
                    if (param.Length == 0)
                    {
                        continue;
                    }

                    // The TYPE is the leading whitespace-delimited token; strip generic args (`<...>`)
                    // and trailing nullability (`?`) so it matches the bare type name in the allowlist.
                    var typeToken = param.Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries)[0];
                    var normalized = typeToken.Split('<')[0].TrimEnd('?');
                    deps.Add((driver, normalized));
                }
            }
        }

        return deps;
    }

    /// <summary>
    /// Walks up from the test assembly's base directory to the repo root, identified by the committed
    /// solution at <c>engine/Babelstone.slnx</c> — the SAME disk-marker pattern
    /// <see cref="EngineFamilyAgnosticTests"/> uses.
    /// </summary>
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "engine", "Babelstone.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName
            ?? throw new InvalidOperationException(
                $"repo root (containing engine/Babelstone.slnx) not found from {AppContext.BaseDirectory}");
    }
}
