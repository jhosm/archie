using Microsoft.CodeAnalysis;

namespace Babelstone.Engine.Analyzers;

/// <summary>
/// Diagnostic descriptors for the handler-purity analysers (ADR-PC-010 §P5; feature-design
/// event-store §5.1). These are the BUILD-TIME half of the DETERMINISM_GATE
/// (commitment-catalogue); the runtime half is the fixture-replay test. All three are
/// warnings, and the engine builds warnings-as-errors, so a violation fails the build.
/// </summary>
internal static class EngineDiagnostics
{
    public const string Category = "Babelstone.Engine";

    public const string ClockId = "BENG001";
    public const string IoId = "BENG002";
    public const string RandomnessId = "BENG003";

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
}
