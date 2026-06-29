using Microsoft.CodeAnalysis;

namespace Babelstone.Engine.Analyzers;

/// <summary>
/// Shared diagnostic descriptors for the build-time engine analysers (BENG001–005). Each is a
/// warning and the engine builds warnings-as-errors, so any one fails the build. They back three
/// commitments: BENG001–003 are the build-time half of DETERMINISM_GATE (ADR-PC-010 §P5;
/// feature-design event-store §5.1 — the runtime half is the fixture-replay test), BENG004 is the
/// structural half of NO_CLOCK_DRIVEN_ENGINE_SIGNAL (ADR-PC-023 §D1), and BENG005 is the
/// build-time tripwire leg of OBS_NO_PII_ATTRS (ADR-IC-007 §P4). Per-descriptor rationale is on
/// each field below.
/// </summary>
internal static class EngineDiagnostics
{
    public const string Category = "Babelstone.Engine";

    public const string ClockId = "BENG001";
    public const string IoId = "BENG002";
    public const string RandomnessId = "BENG003";
    public const string ClockDrivenSignalId = "BENG004";
    public const string PiiTelemetryAttributeId = "BENG005";

    public static readonly DiagnosticDescriptor ClockInHandler = new(
        id: ClockId,
        title: "Event handlers must not read the clock",
        messageFormat: "'{0}' reads the clock inside an event handler — handlers are pure (state, event) → state; inject time as an event field or runtime input (ADR-PC-010 §P5)",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "ADR-PC-010 §P5 / event-store §5.1: a handler that reads the clock is non-deterministic — replay at a different wall-clock time produces different state. Time enters as a value (the event's valid_time, or a runtime-supplied input), never by reading DateTime/DateTimeOffset.Now/UtcNow, Stopwatch, or Environment.TickCount.")
    ;

    public static readonly DiagnosticDescriptor IoInHandler = new(
        id: IoId,
        title: "Event handlers must not perform I/O",
        messageFormat: "'{0}' performs I/O inside an event handler — handlers are pure; do side effects as scheduled events the runtime routes (ADR-PC-010 §P5)",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "ADR-PC-010 §P5 / event-store §5.1: a handler that reads the database, calls the network, or touches the filesystem cannot be replayed deterministically and couples the fold to live infrastructure. Side effects come back as ScheduledEffect data the runtime turns into outbox rows — never HttpClient, File, Directory, Process.Start, or a DbConnection in the handler body.")
    ;

    public static readonly DiagnosticDescriptor RandomnessInHandler = new(
        id: RandomnessId,
        title: "Event handlers must not use randomness",
        messageFormat: "'{0}' uses randomness inside an event handler — handlers are pure; ids/values are supplied to the handler, not generated in it (ADR-PC-010 §P5)",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "ADR-PC-010 §P5 / event-store §5.1: Random, RandomNumberGenerator, and Guid.NewGuid() make a handler non-deterministic — replay yields different output. Identifiers and random values are minted by the runtime on the write path and carried into the event, so the fold sees fixed values.")
    ;

    public static readonly DiagnosticDescriptor ClockDrivenSignal = new(
        id: ClockDrivenSignalId,
        title: "Engine signals must not be clock-driven",
        messageFormat: "'{0}' is emitted from a clock read — an engine signal must be caused by a command/domain fact, not by the passage of time; expose state and let a downstream consumer derive the timing (ADR-PC-023 §D1)",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "ADR-PC-023 §D1 (NO_CLOCK_DRIVEN_ENGINE_SIGNAL): a signal whose only cause is a date arriving — a clock tick / scheduler firing — is not a fact about the aggregate and cannot be reproduced deterministically on replay (it depends on when the rebuild runs). This is the STRUCTURAL half the lexical name-scan cannot give: it does not care what the emitted DomainEvent/ScheduledEffect is named (an off-list DepositMaturityForecast is caught), only that a clock/scheduler/timer read (DateTime/DateTimeOffset.Now/UtcNow/Today, TimeProvider.GetUtcNow, Stopwatch, Environment.TickCount) flows into its construction. Emits caused by a command or domain event (time carried as an event field or input value) are clean. The temporal signal is a projection read, owned downstream.")
    ;

    public static readonly DiagnosticDescriptor PiiTelemetryAttribute = new(
        id: PiiTelemetryAttributeId,
        title: "Telemetry attributes must not carry PII",
        messageFormat: "telemetry attribute '{0}' carries PII (NIF, IBAN, account number, customer name, or e-mail) — spans/logs admit only the structural babelstone.* operational tier; carry a salted babelstone.subject_pseudonym, and money as integer cents (ADR-IC-007 §P4)",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "ADR-IC-007 §P4 / ADR-PC-004 §P2 (OBS_NO_PII_ATTRS): the distributed tracing backend aggregates span/log attributes into one searchable, regulated store, so a single attribute key like deposit.client_nif / core.account / client_email — or a constant IBAN/NIF/e-mail VALUE stamped onto a tag — turns the trace store into a GDPR incident with an Article-17 erasure obligation. This is the build-time mechanical half of §P4's classification rule: a telemetry attribute setter (Activity.SetTag/AddTag/SetBaggage, ActivityTagsCollection/TagList.Add) whose constant key carries a PII fragment (nif, iban, account, name, email, client, phone, address, tax_id) outside the admitted babelstone.* operational tier, or whose constant value is IBAN/NIF/e-mail-shaped, fails the build. Where a span must reference a customer it carries a salted one-way babelstone.subject_pseudonym (ADR-IC-016 plane (iii) §8), never the raw id; money rides as integer cents under babelstone.*_cents.")
    ;
}
