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
    /// Clock-driven "about-to-happen" naming a family event type MUST NOT carry (ADR-PC-023 §D1):
    /// a signal whose only cause is "a date arrived" is not a fact about the aggregate and is not
    /// engine-emitted. The forbidden suffixes name the non-fact tense ("approaching", "due",
    /// "upcoming", "imminent", "expiring", "reminder") the ADR's <c>DepositMaturityApproaching</c> /
    /// <c>PaymentDue</c> examples illustrate. Matched against the event type's terminal word so a
    /// legitimate past-tense fact (<c>...Matured</c>, <c>...Accrued</c>, <c>...Paid</c>) never trips.
    /// </summary>
    private static readonly string[] ForbiddenClockDrivenSuffixes =
        ["Approaching", "Upcoming", "Due", "Imminent", "Expiring", "Pending", "Reminder", "Soon"];

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
            var text = File.ReadAllText(file);
            foreach (var primitive in schedulerPrimitives)
            {
                if (text.Contains(primitive, StringComparison.Ordinal))
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
    /// </summary>
    [Fact]
    public void Family_decider_and_append_path_holds_no_gl_or_notify_gate()
    {
        var repoRoot = RepoRoot();
        var familyAppDir = Path.Combine(
            repoRoot, "families", "term-deposit", "src",
            "Babelstone.Families.TermDeposit.Application");
        var familyDir = Path.Combine(
            repoRoot, "families", "term-deposit", "src", "Babelstone.Families.TermDeposit");
        Assert.True(Directory.Exists(familyAppDir), $"family application source not found on disk: {familyAppDir}");
        Assert.True(Directory.Exists(familyDir), $"family source not found on disk: {familyDir}");

        // GL/notify port symbols that, if referenced from the decide/append path, would let a GL or
        // notification outcome gate or unwind the producing flow. Word-boundary matched so a benign
        // identifier (e.g. a comment mentioning "notification") that is not a port symbol use does not
        // trip — these are PORT type names / synchronous-call shapes, not prose.
        var gatingPortSymbols = new[]
        {
            "IGeneralLedger", "ILedgerPort", "IGlPort", "IGlPostingPort", "PostToLedger",
            "INotificationPort", "INotificationGate", "INotificationSink", "INotifier",
            "SendNotificationAsync", "PostGlAsync", "AwaitGlAck", "AwaitNotificationAck",
        };

        var violations = new List<string>();
        foreach (var dir in new[] { familyAppDir, familyDir })
        {
            foreach (var file in Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories))
            {
                // Strip line comments so prose ("the notification fires post-commit") cannot trip a
                // symbol match — only the executable surface matters for the gating structure.
                var code = StripLineComments(File.ReadAllText(file));
                foreach (var symbol in gatingPortSymbols)
                {
                    if (Regex.IsMatch(code, $@"\b{Regex.Escape(symbol)}\b"))
                    {
                        violations.Add($"{Path.GetFileName(file)} references gating port symbol '{symbol}'");
                    }
                }
            }
        }

        Assert.True(
            violations.Count == 0,
            "ADR-PC-012 slot 5 (GL_POST_FLAG_NEVER_GATES) + ADR-PC-025 slot 5 (NOTIFY_POST_FLAG_NEVER_GATES): "
            + "the family decide/append path must hold no synchronous GL/notify port — a GL reject or a "
            + "notification failure is post-flag, downstream of the local commit, reached only via the "
            + "outbox; it never gates or unwinds the producing flow. The PRE_CONTRACTUAL FIN gate is the "
            + "saga carve-out, not on this path. Offending references:\n  " + string.Join("\n  ", violations));
    }

    /// <summary>
    /// GL_POST_FLAG_NEVER_GATES + NOTIFY_POST_FLAG_NEVER_GATES — positive half: emission goes through
    /// the OUTBOX. The append path the family service drives (<see cref="AggregateRuntime{TState}"/>)
    /// writes the event row AND its outbox row in one transaction (ADR-PC-001 §P2 / ES_ATOMIC_APPEND_OUTBOX),
    /// and the outbox is the ONLY emission channel — there is no synchronous publish on the write path.
    /// Asserts structurally that <see cref="AggregateRuntime{TState}.AppendAsync"/> builds an
    /// <c>OutboxRow</c> per event and commits via the sink, so every emitted signal rides the
    /// post-commit, fire-and-forget outbox rather than a gating synchronous call.
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
    /// A PII fragment match that excludes the structural false positives in this contract: a
    /// <c>*_ref</c> reference (e.g. <c>heir_case_ref</c>) is an OPAQUE handle the engine resolves
    /// internally — by design NOT PII — and <c>tax_cents</c> / <c>tax_basis_points</c> is a money/rate
    /// amount, not the <c>tax_id</c> identifier the fragment guards against. The fragment test still
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

        // "tax" only trips via the tax_id fragment; a tax amount/rate (tax_cents, tax_basis_points,
        // tax_rate) is structural money, not an identifier.
        if (fragment == "tax_id" && !loweredFieldName.Contains("tax_id", StringComparison.Ordinal))
        {
            return false;
        }

        return true;
    }

    /// <summary>The forbidden clock-driven suffix an event type name ends with, or null if none.</summary>
    private static string? MatchedClockDrivenSuffix(string eventTypeName)
        => ForbiddenClockDrivenSuffixes.FirstOrDefault(
            suffix => eventTypeName.EndsWith(suffix, StringComparison.Ordinal));

    /// <summary>
    /// The family <see cref="DomainEvent"/> type names declared in <c>families/**/Events.cs</c>, read
    /// off disk the same way <see cref="EngineFamilyAgnosticTests"/> reads the spine off its csproj —
    /// no family ProjectReference from this engine-spine test project (that would itself break
    /// ENGINE_FAMILY_AGNOSTIC). Keys off the <c>public sealed record X(...) : DomainEvent</c> shape.
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
            foreach (Match match in Regex.Matches(
                source, @"record\s+([A-Z][A-Za-z0-9]*)\s*\([^)]*\)\s*:\s*DomainEvent", RegexOptions.Singleline))
            {
                names.Add(match.Groups[1].Value);
            }
        }

        return names;
    }

    /// <summary>Strips C# line comments so prose in comments cannot match an executable-symbol scan.</summary>
    private static string StripLineComments(string source)
        => Regex.Replace(source, @"//.*?$", string.Empty, RegexOptions.Multiline);

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
