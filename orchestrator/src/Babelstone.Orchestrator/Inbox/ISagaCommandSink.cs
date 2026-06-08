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
    Task EmitAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid processId,
        string commandType,
        Guid causationMessageId,
        Guid? correlationId,
        CancellationToken ct = default);
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
        CancellationToken ct = default)
    {
        _emitted.Add(new EmittedCommand(processId, commandType, causationMessageId, correlationId));
        return Task.CompletedTask;
    }
}

/// <summary>One command the saga emitted (the identity trio + the command type), for the
/// <see cref="RecordingCommandSink"/>. PII-free by construction.</summary>
/// <param name="ProcessId">The saga instance the command belongs to.</param>
/// <param name="CommandType">The command name the state machine decided.</param>
/// <param name="CausationMessageId">The triggering event's message id (ADR-IC-003 §P7).</param>
/// <param name="CorrelationId">The trace correlation reference carried through.</param>
public sealed record EmittedCommand(
    Guid ProcessId,
    string CommandType,
    Guid CausationMessageId,
    Guid? CorrelationId);
