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

        // ADR-PC-043 §D5 (bd u79p.13/.21): the engine-CA CREDIT leg carries the customer's PROMOTED destination
        // account_ref + amount, forwarded UNTOUCHED into the CA-apply command body — the fix that makes an
        // engine-CA leg's AccountRef equal the promoted Movement.AccountRef, never the ACCT-{processId}
        // placeholder (SETTLEMENT_LEG_ACCOUNT_REF_PROMOTED, CA-17), and lands the source Movement.Amount (the
        // in-band WRONG-AMOUNT guard). Threaded ONLY for the CONFIRM-CREDIT leg: the confirmation-gated credit
        // fires on the START advance (SettlementStarted → ConfirmingCredit), where the Movement-bearing event's
        // promoted headers are directly in scope. The debit legs' irreversible ConfirmDebit fires on a LATER
        // advance off a synthesized result event that does not yet forward these values (that propagation is the
        // bd u79p.21 debit-path follow-up), so a debit keeps the existing placeholder path — never a
        // reserve/confirm mismatch. The IntentId is the leg's process id (hex), so the intent-derived credit
        // reference stays BYTE-IDENTICAL to the placeholder path's Derive(CreditPrefix, processId): the
        // ADR-PC-043 slot-4 exactly-once key is unchanged, ONLY the destination account_ref + amount move.
        SettlementIntent? intent =
            commandType == SettlementProcess.ConfirmCredit && !string.IsNullOrWhiteSpace(settlementAccountRef)
                ? new SettlementIntent(processId.ToString("N"), settlementAmountCents ?? 0L, settlementAccountRef)
                : null;

        // The LOGICAL payload body: the settlement command, byte-stable (no minted GUID, no wall clock). The
        // factory must cover every command the settlement state machine emits — a miss is a fail-closed
        // wiring error, not a silent empty body.
        var payload = SettlementCommandPayloadFactory.Build(
                commandType, processId, causationMessageId, correlationId, intent)
            ?? throw new InvalidOperationException(
                $"No settlement command-payload recipe for '{commandType}' on saga {processId}; the factory " +
                "must cover every command the SettlementProcess state machine emits (ADR-PC-032).");

        // The substrate's saga_outbox store owns the row write + the operational message_id mint (the row
        // commits atomically on this saga transaction, ADR-IC-003 §P1). The gateway-attested SCA claims (bd
        // babelstone-ls44; ADR-IC-010 §P8) ride the row as operational columns the dispatcher re-emits onto
        // the cash leg's delivery — the freshness gate ADR-PC-032 Amendment A7/A8 places on the Originated
        // cash leg reads them. Threaded through verbatim here; the freshness re-check is the receiver's (bd
        // babelstone-t7o3.19), not the substrate's (attest, don't deny — A8).
        await _outbox.AppendAsync(
            connection, transaction, processId, commandType, causationMessageId, correlationId,
            payload.ToBytes(), traceParent, ct, scaAcr, scaAuthTime);
    }
}
