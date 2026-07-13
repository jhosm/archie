using Babelstone.Orchestrator.Inbox;
using Npgsql;

namespace Babelstone.Orchestrator.Saga.Settlement;

/// <summary>
/// The <see cref="ISagaTypedCommandSink"/> for the substrate-owned <see cref="SettlementProcess"/> saga
/// (ADR-PC-032; the settlement counterpart of the constitution/renewal sinks). It owns ONLY the
/// settlement-specific command-payload assembly (<see cref="SettlementCommandPayloadFactory"/>); the row
/// write itself — appending to the substrate-owned <c>saga_outbox</c> store on the saga transaction — is
/// delegated to the substrate's <see cref="SagaOutboxWriter"/> (ADR-IC-018 §D2). The dispatcher
/// (<c>SagaCommandDispatchDrainer</c>) is the only reader; it POSTs each row to the Core ACL leg the
/// <see cref="SettlementCommandRouter"/> resolves.
/// </summary>
/// <remarks>
/// UNLIKE the constitution/renewal sinks, this one lives IN the substrate — it is the family-agnostic
/// settlement sink the substrate-owned saga needs, naming no family (the narrowed ORCH-2 allow-list). The
/// payload is byte-stable (ADR-PC-010 §P5): the factory builds it from the process id alone (every reference
/// is the deterministic process-id derivation, no Guid.NewGuid, no wall clock), so a crash-recovery reissue
/// is replayable. The ONE freshly minted value — the delivery <c>message_id</c> the ACL dedups on
/// (ADR-PC-029 slot 4) — is minted by the <see cref="SagaOutboxWriter"/> as an outbox COLUMN, never the body.
/// PII-free (ADR-PC-004 §P2): every reference is opaque.
/// </remarks>
public sealed class SettlementCommandOutboxSink(SagaOutboxWriter? outbox = null) : ISagaTypedCommandSink
{
    private readonly SagaOutboxWriter _outbox = outbox ?? new SagaOutboxWriter();

    /// <inheritdoc />
    public string SagaType => SettlementProcess.Type;

    /// <inheritdoc />
    public async Task EmitAsync(
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
        ArgumentException.ThrowIfNullOrWhiteSpace(commandType);

        // ADR-PC-043 §D5 (bd u79p.13/.21/.22): an engine-CA settlement leg carries the customer's PROMOTED
        // destination account_ref + amount, forwarded UNTOUCHED into the CA-apply command body — the fix that
        // makes an engine-CA leg's AccountRef equal the promoted Movement.AccountRef, never the
        // ACCT-{processId} placeholder (SETTLEMENT_LEG_ACCOUNT_REF_PROMOTED, CA-17), and lands the source
        // Movement.Amount (the in-band WRONG-AMOUNT guard). Built for EVERY leg that carries a promoted
        // account_ref — the confirmation-gated CREDIT (which fires on the START advance), AND the funds-gated
        // DEBIT legs (reserve + confirm-debit). The credit and the reserve have the promoted headers directly
        // in scope on the START advance; the irreversible ConfirmDebit fires on a LATER advance off a
        // synthesized BalanceReserved result event, so the dispatcher FORWARD-PROPAGATES the promoted values
        // onto that event (persisted below as saga_outbox columns and re-emitted as the movement headers the
        // next advance reads, bd u79p.22) — the confirm re-threads the SAME intent, so reserve and confirm
        // never mismatch. The IntentId is the leg's process id (hex), so the intent-derived reference stays
        // BYTE-IDENTICAL to the placeholder path's Derive(prefix, processId): the ADR-PC-043 slot-4 exactly-once
        // key is unchanged, ONLY the destination account_ref + amount + counterparty target move.
        SettlementIntent? intent =
            string.IsNullOrWhiteSpace(settlementAccountRef)
                ? null
                : new SettlementIntent(processId.ToString("N"), settlementAmountCents ?? 0L, settlementAccountRef);

        // The LOGICAL payload body: the settlement command, byte-stable (no minted GUID, no wall clock). The
        // factory must cover every command the settlement state machine emits — a miss is a fail-closed
        // wiring error, not a silent empty body.
        var payload = SettlementCommandPayloadFactory.Build(
                commandType, processId, causationMessageId, correlationId, intent)
            ?? throw new InvalidOperationException(
                $"No settlement command-payload recipe for '{commandType}' on saga {processId}; the factory " +
                "must cover every command the SettlementProcess state machine emits (ADR-PC-032).");

        // The substrate's saga_outbox store owns the row write + the operational message_id mint (the row
        // commits atomically on this saga transaction, ADR-IC-003 §P1). Two families of operational columns
        // ride the row for the dispatcher to re-emit downstream, never the byte-stable body:
        //   • the gateway-attested SCA claims (bd babelstone-ls44; ADR-IC-010 §P8) — re-emitted as the cash
        //     leg's outbound X-SCA-* HTTP headers the ADR-PC-032 §A7/§A8 freshness gate reads (attest, don't
        //     deny — the receiver is the deny point, bd babelstone-t7o3.19);
        //   • the promoted engine-CA destination account_ref + amount (bd babelstone-u79p.22; ADR-PC-043 §D5)
        //     — re-emitted onto the SYNTHESIZED result event's movement headers so the reserve→confirm hop
        //     forward-propagates the destination (the debit path's ConfirmDebit fires on a header-less later
        //     advance). Null for every legacy / non-engine-CA leg.
        await _outbox.AppendAsync(
            connection, transaction, processId, commandType, causationMessageId, correlationId,
            payload.ToBytes(), traceParent, ct, scaAcr, scaAuthTime, settlementAccountRef, settlementAmountCents);
    }
}
