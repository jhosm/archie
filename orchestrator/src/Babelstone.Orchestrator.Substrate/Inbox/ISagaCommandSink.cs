using Npgsql;

namespace Babelstone.Orchestrator.Inbox;

/// <summary>
/// Where a saga's emitted commands go (ADR-IC-003 §P1 "Outbox for commands: saga-emitted
/// commands use the same outbox mechanism as all other services … not a separate publish
/// path"; §P7 the identity trio rides every emission). The advance handler hands each
/// command the state machine decided (ADR-IC-003 §P2) to this sink INSIDE the saga
/// transaction, so the command's outbox row commits ATOMICALLY with the state move and the
/// dedup row — no command escapes for a transition that rolled back.
/// </summary>
/// <remarks>
/// The substrate defines the SEAM (this issue, babelstone-mj2i); H.2 (babelstone-n55u) wires
/// the real outbox-row writer and the concrete command payloads behind it. The default
/// <see cref="RecordingCommandSink"/> proves the seam — it captures what WOULD be emitted —
/// without asserting a payload shape, the same way the engine's <c>NullInboxMessageHandler</c>
/// stands in until a real handler lands.
/// </remarks>
public interface ISagaCommandSink
{
    /// <summary>
    /// Enqueue one command the saga decided to emit, on the supplied transaction. The
    /// command NAME is the contract here; the payload is the implementer's concern and MUST
    /// carry the identity trio (correlation/causation/new message id, ADR-IC-003 §P7) and NO
    /// PII (ADR-PC-004 §P2 — the durable bus carries references).
    /// </summary>
    /// <param name="traceParent">The outbound W3C <c>traceparent</c> header (H.5) — the
    /// advance span's context, so the downstream consumer threads its spans under this saga's
    /// trace (ADR-IC-007 Layer 1). Operational, not PII; null when no tracer was listening
    /// (no span to propagate). The implementer persists it on the outbox row for the drain to
    /// re-emit as the outbound Kafka header.</param>
    /// <param name="scaAcr">The gateway-attested OIDC <c>acr</c> claim (bd babelstone-ls44;
    /// ADR-IC-010 §P8 A10) for a money-mover command (maturity / interest). The dispatcher
    /// re-emits it as the outbound <c>X-SCA-Acr</c> header the engine's step-up-SCA gate reads,
    /// threading the SAME gateway-attested claims through the saga lane that the engine-direct
    /// path already enforces. Operational, not PII; null for every command that carries no SCA
    /// attestation (the common case — then the engine gate 422s if the command is a money-mover).</param>
    /// <param name="scaAuthTime">The gateway-attested OIDC <c>auth_time</c> claim (seconds since the
    /// Unix epoch) for a money-mover command. The dispatcher re-emits it as the outbound
    /// <c>X-SCA-Auth-Time</c> header; the engine re-checks freshness at dispatch time, so a stale
    /// value is fail-closed 422'd there. Operational, not PII; null when no SCA was attested.</param>
    /// <param name="settlementAccountRef">The engine-CA leg's PROMOTED destination <c>account_ref</c> — the
    /// customer's persistent conta-à-ordem reference the source family promoted onto the Movement-bearing event's
    /// CloudEvents headers (ADR-PC-043 §D5 amendment 2026-07-11; read off the leg's reduced
    /// <c>movementaccountrefs</c> by the advance handler). The settlement sink forwards it untouched into the
    /// CA-apply command body as the credit/debit DESTINATION — never a routing input (routing keys on
    /// <c>settlementtarget</c> alone, ADR-IC-018 §D5). Opaque ref, not PII; null on the legacy-DDA path and for
    /// every non-settlement sink (which resolve the account from their own business reference).</param>
    /// <param name="settlementAmountCents">The engine-CA leg's PROMOTED integer-cents amount — the source
    /// <c>Movement.Amount</c> the CA writer lands, the in-band WRONG-AMOUNT guard (ADR-PC-043 §D5; read off the
    /// leg's reduced <c>movementamounts</c>). Null on the legacy-DDA path and for every non-settlement sink.</param>
    Task EmitAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid processId,
        string commandType,
        Guid causationMessageId,
        Guid? correlationId,
        CancellationToken ct = default,
        string? traceParent = null,
        string? scaAcr = null,
        long? scaAuthTime = null,
        string? settlementAccountRef = null,
        long? settlementAmountCents = null);
}

/// <summary>
/// An <see cref="ISagaCommandSink"/> that serves exactly ONE saga type (bd babelstone-mtto PR2) — the
/// sink-side cousin of <see cref="Dispatch.ISagaCommandRouter"/> and <see cref="Saga.IResultEventBridge"/>.
/// Each saga type's command bodies are assembled by its OWN family payload factory (the constitution
/// sink builds the business-reference payloads; the renewal sink builds the renewal wire bodies), so a
/// multi-saga host needs the SAME <c>saga_type → sink</c> routing the machine/router/bridge registries
/// already use. The <see cref="CompositeSagaCommandSink"/> collects every registered typed sink and the
/// advance handler routes by the advancing saga's <c>saga_type</c>. The substrate names no family.
/// </summary>
public interface ISagaTypedCommandSink : ISagaCommandSink
{
    /// <summary>The saga type this sink's command-payload assembly serves — matches
    /// <see cref="Saga.ISagaStateMachine.SagaType"/> and the persisted <c>saga_state.saga_type</c>.</summary>
    string SagaType { get; }
}

/// <summary>
/// Routes a saga's command emission to the <see cref="ISagaTypedCommandSink"/> for its saga type (bd
/// babelstone-mtto PR2). Built from every registered typed sink into a <c>saga_type → sink</c> registry;
/// the advance handler resolves the sink for the advancing saga by <c>saga_type</c> before emitting. A
/// duplicate <see cref="ISagaTypedCommandSink.SagaType"/> is a wiring error and throws (the registry must
/// be a function — the same stance the machine/router/bridge registries take). This composite itself
/// implements <see cref="ISagaCommandSink"/> only so it can be the handler's single injected sink; its
/// own <see cref="EmitAsync"/> requires the caller (the handler) to have selected the right sub-sink via
/// <see cref="For"/>, never a bare emit with no saga type (which throws).
/// </summary>
public sealed class CompositeSagaCommandSink : ISagaCommandSink
{
    private readonly IReadOnlyDictionary<string, ISagaTypedCommandSink> _sinks;

    public CompositeSagaCommandSink(IEnumerable<ISagaTypedCommandSink> sinks)
    {
        ArgumentNullException.ThrowIfNull(sinks);
        var map = new Dictionary<string, ISagaTypedCommandSink>(StringComparer.Ordinal);
        foreach (var sink in sinks)
        {
            if (!map.TryAdd(sink.SagaType, sink))
            {
                throw new InvalidOperationException(
                    $"Duplicate ISagaTypedCommandSink for saga_type '{sink.SagaType}': the saga-type → " +
                    "sink registry must be a function (bd babelstone-mtto PR2).");
            }
        }

        if (map.Count == 0)
        {
            throw new ArgumentException("At least one ISagaTypedCommandSink must be registered.", nameof(sinks));
        }

        _sinks = map;
    }

    /// <summary>The sink for <paramref name="sagaType"/>, or a fail-closed error if none is registered
    /// (a saga whose type has no sink cannot emit — the substrate cannot assemble a payload it has no
    /// factory for, never a silent skip).</summary>
    public ISagaCommandSink For(string sagaType) =>
        _sinks.TryGetValue(sagaType, out var sink)
            ? sink
            : throw new InvalidOperationException(
                $"No ISagaTypedCommandSink registered for saga_type '{sagaType}'. Register the family's " +
                "command sink in the host (bd babelstone-mtto PR2).");

    /// <summary>NOT supported on the composite — the advance handler must select the typed sub-sink via
    /// <see cref="For"/> (it knows the advancing saga's type). A bare emit has no saga type to route on.</summary>
    public Task EmitAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid processId,
        string commandType,
        Guid causationMessageId,
        Guid? correlationId,
        CancellationToken ct = default,
        string? traceParent = null,
        string? scaAcr = null,
        long? scaAuthTime = null,
        string? settlementAccountRef = null,
        long? settlementAmountCents = null)
        => throw new NotSupportedException(
            "CompositeSagaCommandSink routes by saga_type — call For(sagaType).EmitAsync(...). The advance "
            + "handler selects the typed sub-sink; a bare emit carries no saga type to route on.");
}

/// <summary>
/// The default <see cref="ISagaCommandSink"/>: an in-memory recorder that captures the
/// commands a saga would emit without writing an outbox row (no real fan-out yet). It proves
/// the advance handler decides and routes the right commands — the substrate's testable
/// stand-in until H.2 plugs in the real outbox writer.
/// </summary>
public sealed class RecordingCommandSink : ISagaCommandSink
{
    private readonly List<EmittedCommand> _emitted = [];

    /// <summary>The commands captured so far, in emission order. For inspection and tests.</summary>
    public IReadOnlyList<EmittedCommand> Emitted => _emitted;

    /// <inheritdoc />
    public Task EmitAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid processId,
        string commandType,
        Guid causationMessageId,
        Guid? correlationId,
        CancellationToken ct = default,
        string? traceParent = null,
        string? scaAcr = null,
        long? scaAuthTime = null,
        string? settlementAccountRef = null,
        long? settlementAmountCents = null)
    {
        _emitted.Add(new EmittedCommand(
            processId, commandType, causationMessageId, correlationId, traceParent, scaAcr, scaAuthTime,
            settlementAccountRef, settlementAmountCents));
        return Task.CompletedTask;
    }
}

/// <summary>One command the saga emitted (the identity trio + the command type), for the
/// <see cref="RecordingCommandSink"/>. PII-free by construction.</summary>
/// <param name="ProcessId">The saga instance the command belongs to.</param>
/// <param name="CommandType">The command name the state machine decided.</param>
/// <param name="CausationMessageId">The triggering event's message id (ADR-IC-003 §P7).</param>
/// <param name="CorrelationId">The trace correlation reference carried through.</param>
/// <param name="TraceParent">The outbound W3C <c>traceparent</c> the emission propagates (H.5),
/// or null when no tracer was listening. Operational, not PII.</param>
/// <param name="ScaAcr">The gateway-attested OIDC <c>acr</c> claim a money-mover emission propagates
/// (bd babelstone-ls44), or null when no SCA was attested. Operational, not PII.</param>
/// <param name="ScaAuthTime">The gateway-attested OIDC <c>auth_time</c> claim (Unix seconds) a
/// money-mover emission propagates, or null when no SCA was attested. Operational, not PII.</param>
/// <param name="SettlementAccountRef">The engine-CA leg's promoted destination <c>account_ref</c> a settlement
/// emission carries (ADR-PC-043 §D5), or null on the legacy-DDA path / a non-settlement emission. Opaque, not PII.</param>
/// <param name="SettlementAmountCents">The engine-CA leg's promoted integer-cents amount a settlement emission
/// carries (the WRONG-AMOUNT guard), or null on the legacy-DDA path / a non-settlement emission.</param>
public sealed record EmittedCommand(
    Guid ProcessId,
    string CommandType,
    Guid CausationMessageId,
    Guid? CorrelationId,
    string? TraceParent = null,
    string? ScaAcr = null,
    long? ScaAuthTime = null,
    string? SettlementAccountRef = null,
    long? SettlementAmountCents = null);
