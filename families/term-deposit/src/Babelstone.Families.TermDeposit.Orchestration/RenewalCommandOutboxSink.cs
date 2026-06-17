using Babelstone.Orchestrator.Inbox;
using Npgsql;

namespace Babelstone.Families.TermDeposit.Orchestration;

/// <summary>
/// The <see cref="ISagaCommandSink"/> for the <see cref="RenewalProcess"/> saga (bd babelstone-mtto;
/// the renewal counterpart of <see cref="SagaCommandOutboxSink"/>). It owns ONLY the renewal-specific
/// command-payload assembly (<see cref="RenewalCommandPayloadFactory"/>); the row write itself —
/// appending to the substrate-owned <c>saga_outbox</c> store on the saga transaction — is delegated to
/// the substrate's <see cref="SagaOutboxWriter"/> (ADR-IC-018 §D2 names <c>saga_outbox</c> a substrate
/// store; the writer commits the row ATOMICALLY with the state move and the dedup row, ADR-IC-003 §P1).
/// The dispatcher (<c>SagaCommandDispatchDrainer</c>) is the only reader; it POSTs the row to the
/// engine's renewal legs, substituting the row's process_id into the {process_id} path template
/// (<see cref="RenewalCommandRouter"/>).
/// </summary>
/// <remarks>
/// <para>
/// <b>The payload is byte-stable (ADR-PC-010 §P5, crash-safe).</b> The body is built byte-stably by
/// <see cref="RenewalCommandPayloadFactory"/> from the process id alone (the new deposit id is the
/// deterministic derivation, NO <see cref="Guid.NewGuid"/>; no wall clock — renewed_at is host-stamped
/// engine-side), so re-emitting the same logical command yields identical bytes and a crash-recovery
/// reissue is replayable. The ONE freshly minted value — the delivery <c>message_id</c> the engine dedups
/// on (ADR-PC-029 slot 4) — is minted by the <see cref="SagaOutboxWriter"/> as an outbox COLUMN, NEVER the
/// body.
/// </para>
/// <para>
/// <b>No per-saga state, NO product/role/funding config (UNLIKE the constitution sink).</b> The renewal
/// saga is EVENT-AUTO-STARTED off <c>DepositMatured</c> (no edge to pin references), and the engine
/// resolves EVERY renewal fact — including the product code, pricing role and funding account — from the
/// Matured closing deposit it loads (ADR-PC-009; bd babelstone-mtto.5). So this sink carries no
/// product-family knowledge (ADR-IC-003 §A7): the command body is the minimal <c>{ new_deposit_id }</c>.
/// PII-free (ADR-PC-004 §P2): a single derived deposit id — never a NIF/IBAN/name.
/// </para>
/// </remarks>
public sealed class RenewalCommandOutboxSink(SagaOutboxWriter? outbox = null) : ISagaTypedCommandSink
{
    private readonly SagaOutboxWriter _outbox = outbox ?? new SagaOutboxWriter();

    /// <inheritdoc />
    public string SagaType => RenewalProcess.Type;

    /// <inheritdoc />
    public async Task EmitAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid processId,
        string commandType,
        Guid causationMessageId,
        Guid? correlationId,
        CancellationToken ct = default,
        string? traceParent = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(commandType);

        // The LOGICAL payload body: the engine's renewal wire body, byte-stable (no minted GUID, no wall
        // clock). The factory must cover every command the renewal state machine emits — a miss is a
        // fail-closed wiring error, not a silent empty body.
        var payload = RenewalCommandPayloadFactory.Build(commandType, processId)
            ?? throw new InvalidOperationException(
                $"No renewal command-payload recipe for '{commandType}' on saga {processId}; the factory " +
                "must cover every command the RenewalProcess state machine emits (bd babelstone-mtto).");

        // The substrate's saga_outbox store owns the row write + the operational message_id mint
        // (ADR-IC-018 §D2; the row commits atomically on this saga transaction, ADR-IC-003 §P1). This
        // sink owns ONLY the family-specific payload assembly above.
        await _outbox.AppendAsync(
            connection, transaction, processId, commandType, causationMessageId, correlationId, payload, traceParent, ct);
    }
}
