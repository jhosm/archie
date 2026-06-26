using Babelstone.Orchestrator.Saga.Settlement;

namespace Babelstone.Families.TermDeposit.Orchestration;

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
    // The derived-reference prefixes + the derivation live in the ONE shared substrate
    // SettlementReferences home (feature-design §8/§10, the rule-of-three cleanup bd babelstone-t7o3.18).
    // The constitution debit leg now composes the SAME shared derivation as the substrate-owned settlement
    // saga, so the constitution's ReserveAccountBalance / ConfirmDebit / QueryCoreDebitStatus and the
    // settlement saga's debit legs derive the IDENTICAL external_reference for the same process id — the
    // cross-saga no-double-debit invariant is structural, not a pair of literals that happen to agree. The
    // constitution-unique ActivateDeposit / ReverseCoreDebit Core-txn reference rides the SAME shared
    // CoreTxnPrefix. NOT minted, no clock (ADR-PC-010 §P5).

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
                ReservationRef = SettlementReferences.Derive(SettlementReferences.ReservationPrefix, processId),
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
                // CoreHoldRef is derived purely from the process id, so a RETRY_PERMITTED reissue of
                // ConfirmDebit out of AWAIT_CORE_CLEARANCE (a DebitNotExecuted clearance) presents the
                // SAME CORE-HOLD-<processId> reference as the original confirm — even though the saga's
                // operational message_id differs per emission. That stable Core-facing reference is the
                // external_reference the ACL folds into its idempotency key (ADR-IC-012 §P4), so the
                // reissue cannot double-debit even in the worst case (the original silently executed but
                // the clearance answered not-executed): the §332 guard returns the recorded core_reference
                // rather than re-debiting. Do NOT mint a fresh ref here — the no-double-debit invariant
                // at v1 (before DEF-1's ACL guard exists) rests on this reference being stable.
                CoreHoldRef = SettlementReferences.Derive(SettlementReferences.CoreHoldPrefix, processId),
            },
            // ActivateDeposit is the ENGINE-bound constitution command (bd babelstone-t7o3.11 / 3k10 /
            // c8d8): its wire body is the engine's MINIMAL ConstituteDepositRequest (snake_case,
            // deposit_id = process_id so ce_subject = process_id), carrying only the product code,
            // principal cents, and funding account. The ENGINE resolves both the structural shape (term /
            // variant / renewal / cadence / role) AND the rate in-transaction — the orchestrator carries
            // no product-family knowledge (the maintainer's Q2 choice, ADR-PC-009). All fields are
            // references/scalars off the pinned reference, so the body is byte-stable.
            ConstitutionProcess.ActivateDeposit => new ActivateDepositCommand
            {
                ProcessId = processId,
                CausationMessageId = causationMessageId,
                CorrelationId = correlationId,
                DepositRef = reference.DepositRef,
                CoreTxnRef = SettlementReferences.Derive(SettlementReferences.CoreTxnPrefix, processId),
                ProductCode = reference.ProductRef,
                PrincipalCents = reference.AmountMinorUnits,
                FundingAccount = reference.SourceAccountRef,
            },
            ConstitutionProcess.ReleaseBalanceReservation => new ReleaseBalanceReservationCommand
            {
                ProcessId = processId,
                CausationMessageId = causationMessageId,
                CorrelationId = correlationId,
                ReservationRef = SettlementReferences.Derive(SettlementReferences.ReservationPrefix, processId),
            },
            ConstitutionProcess.ReverseCoreDebit => new ReverseCoreDebitCommand
            {
                ProcessId = processId,
                CausationMessageId = causationMessageId,
                CorrelationId = correlationId,
                CoreTxnRef = SettlementReferences.Derive(SettlementReferences.CoreTxnPrefix, processId),
            },
            ConstitutionProcess.QueryCoreDebitStatus => new QueryCoreDebitStatusCommand
            {
                ProcessId = processId,
                CausationMessageId = causationMessageId,
                CorrelationId = correlationId,
                DepositRef = reference.DepositRef,
                // The SAME derived Core hold reference the indeterminate ConfirmDebit used, so the
                // clearance query resolves exactly that in-flight operation (deterministic, not minted).
                CoreHoldRef = SettlementReferences.Derive(SettlementReferences.CoreHoldPrefix, processId),
            },
            _ => null,
        };
    }
}
