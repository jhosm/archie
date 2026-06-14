using Babelstone.Orchestrator.Saga;

namespace Babelstone.Orchestrator.Commands;

/// <summary>
/// Assembles the FULL typed <see cref="CommandPayload"/> for a command the saga decided, from the
/// per-saga <see cref="SagaBusinessReference"/> pinned at start (bd babelstone-t7o3.1). This is the
/// "business-reference payloads" half of the issue: where the substrate sink wrote only the minimal
/// <see cref="SagaCommandEnvelopeBody"/> (the process reference + identity trio), this factory builds
/// the real <see cref="ReserveAccountBalanceCommand"/> (with the amount's source account + a derived
/// reservation reference), the <see cref="ActivateDepositCommand"/> (with the deposit/Core-txn
/// references), and so on — the FULL payloads carrying the real business facts.
/// </summary>
/// <remarks>
/// <para>
/// <b>Pure and byte-stable (ADR-PC-010 §P5).</b> The factory is a total function of (command type,
/// process reference, identity trio, pinned business references). Every "derived" reference it
/// produces — the reservation reference, the Core hold/txn references — is a DETERMINISTIC function
/// of the process id (the process id namespaced for the leg), NOT a freshly minted GUID and NOT a
/// wall-clock value. So re-assembling the SAME logical command yields byte-identical bytes (the
/// <c>SagaCommandOutboxSink</c> byte-stability assertion). The minting/clock live in the sink's
/// operational columns, never here.
/// </para>
/// <para>
/// <b>No PII (ADR-PC-004 §P2).</b> Every field the factory sets is a structural reference or an
/// integer-cents amount — the source-account TOKEN, the deposit/product references, a derived
/// reservation/hold/txn reference. NEVER a raw IBAN/NIF/name. The amount itself is NOT placed on a
/// command body that has no amount field: only the leg that needs it (the reserve leg) carries the
/// account it reserves against; the amount rides as the cents the engine's PII boundary already
/// pinned, not as identity data.
/// </para>
/// <para>
/// <b>Fail-closed when references are missing.</b> A command the factory has no business-reference
/// recipe for (or that arrives with no pinned references at all — a consume-loop-started saga) is
/// NOT silently given an empty payload: the caller (the sink) falls back to the seam envelope only
/// when no references were pinned. With references present, an unrecognised command type returns
/// null so the caller can decide (the sink then writes the seam body for it).
/// </para>
/// </remarks>
public static class SagaCommandPayloadFactory
{
    // The derived-reference prefixes (Document 05). Each is a DETERMINISTIC namespacing of the
    // process id for one leg — stable across re-emission, so the body is byte-stable. NOT minted.
    private const string ReservationPrefix = "RSV-";
    private const string CoreHoldPrefix = "CORE-HOLD-";
    private const string CoreTxnPrefix = "CT-";

    /// <summary>
    /// Build the full typed payload for <paramref name="commandType"/>, or null if there is no
    /// business-reference recipe for it (the caller writes the seam envelope for that command). PURE:
    /// no clock, no GUID minting (ADR-PC-010 §P5).
    /// </summary>
    /// <param name="commandType">The command NAME the state machine decided (the same constant the
    /// <see cref="ConstitutionProcess"/> table emits).</param>
    /// <param name="processId">The saga instance the command belongs to.</param>
    /// <param name="causationMessageId">The triggering event's message id (the §P7 causation
    /// reference) — a pre-existing id carried through, never minted here.</param>
    /// <param name="correlationId">The originating request's correlation reference, carried
    /// unchanged through the saga (§P7).</param>
    /// <param name="reference">The pinned, PII-free business references for this saga.</param>
    public static CommandPayload? Build(
        string commandType,
        Guid processId,
        Guid causationMessageId,
        Guid? correlationId,
        SagaBusinessReference reference)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(commandType);
        ArgumentNullException.ThrowIfNull(reference);

        return commandType switch
        {
            ConstitutionProcess.ReserveAccountBalance => new ReserveAccountBalanceCommand
            {
                ProcessId = processId,
                CausationMessageId = causationMessageId,
                CorrelationId = correlationId,
                AccountRef = reference.SourceAccountRef,
                ReservationRef = DerivedRef(ReservationPrefix, processId),
            },
            ConstitutionProcess.ValidateProductLimits => new ValidateProductLimitsCommand
            {
                ProcessId = processId,
                CausationMessageId = causationMessageId,
                CorrelationId = correlationId,
                DepositRef = reference.DepositRef,
                ProductRef = reference.ProductRef,
            },
            ConstitutionProcess.ConfirmDebit => new ConfirmDebitCommand
            {
                ProcessId = processId,
                CausationMessageId = causationMessageId,
                CorrelationId = correlationId,
                CoreHoldRef = DerivedRef(CoreHoldPrefix, processId),
            },
            ConstitutionProcess.ActivateDeposit => new ActivateDepositCommand
            {
                ProcessId = processId,
                CausationMessageId = causationMessageId,
                CorrelationId = correlationId,
                DepositRef = reference.DepositRef,
                CoreTxnRef = DerivedRef(CoreTxnPrefix, processId),
            },
            ConstitutionProcess.ReleaseBalanceReservation => new ReleaseBalanceReservationCommand
            {
                ProcessId = processId,
                CausationMessageId = causationMessageId,
                CorrelationId = correlationId,
                ReservationRef = DerivedRef(ReservationPrefix, processId),
            },
            ConstitutionProcess.ReverseCoreDebit => new ReverseCoreDebitCommand
            {
                ProcessId = processId,
                CausationMessageId = causationMessageId,
                CorrelationId = correlationId,
                CoreTxnRef = DerivedRef(CoreTxnPrefix, processId),
            },
            ConstitutionProcess.QueryCoreDebitStatus => new QueryCoreDebitStatusCommand
            {
                ProcessId = processId,
                CausationMessageId = causationMessageId,
                CorrelationId = correlationId,
                DepositRef = reference.DepositRef,
                // The SAME derived Core hold reference the indeterminate ConfirmDebit used, so the
                // clearance query resolves exactly that in-flight operation (deterministic, not minted).
                CoreHoldRef = DerivedRef(CoreHoldPrefix, processId),
            },
            _ => null,
        };
    }

    // A DETERMINISTIC derived reference for one saga leg: the prefix + the process id's hex. Stable
    // across re-emission (no minted value), so the assembled payload is byte-stable (ADR-PC-010 §P5).
    // The same process id always yields the same leg reference, which is also what makes the ACL's
    // idempotency dedup on it work (the reserve and the release both derive the SAME reservation ref).
    private static string DerivedRef(string prefix, Guid processId) => prefix + processId.ToString("N");
}
