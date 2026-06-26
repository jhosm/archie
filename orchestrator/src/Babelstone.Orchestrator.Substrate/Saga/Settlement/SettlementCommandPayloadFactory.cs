namespace Babelstone.Orchestrator.Saga.Settlement;

/// <summary>
/// Assembles the typed, byte-stable command payloads the substrate-owned <see cref="SettlementProcess"/>
/// saga emits, from the saga's process id and identity trio alone (ADR-PC-032; modelled on the
/// constitution/renewal factories). Every account/hold/credit reference is a DETERMINISTIC namespacing of
/// the process id — never a freshly minted GUID and never a wall clock — so re-emitting the same logical
/// command yields byte-identical bytes (a crash-recovery reissue is replayable; the ACL dedups on the stable
/// reference).
/// </summary>
/// <remarks>
/// <para>
/// <b>Family-agnostic and PII-free (the narrowed ORCH-2 allow-list; ADR-PC-004 §P2).</b> Every reference the
/// factory produces is a process-id-derived, opaque token — never a deposit/loan-typed shape, never a raw
/// IBAN/NIF/name. The factory names no family, exactly as the substrate-owned saga it serves does not.
/// </para>
/// <para>
/// <b>The opaque <c>account_ref</c> seam.</b> ADR-PC-032 carries the real <c>Movement.AccountRef</c> as an
/// opaque reference; the engine relay promotes it (alongside <c>movementdirection</c>) to a CloudEvents
/// extension header on the carrying event, which the saga's start path reads (ADR-IC-018 §D5 — headers, never
/// the payload). At the PLATFORM layer this issue builds (the saga + the settlement command surface), the
/// command body uses the process-id-derived reference as the account/hold/credit token; the wiring that
/// threads the promoted opaque <c>account_ref</c> onto the body lands with each consuming family's
/// Movement migration (bd babelstone-t7o3.13 / t7o3.16, which this saga BLOCKS) — the same staged shape the
/// renewal sink took (minimal body now, the engine/ACL resolves the rest). No PII either way.
/// </para>
/// </remarks>
public static class SettlementCommandPayloadFactory
{
    // The derived-reference prefixes + the derivation live in the ONE shared SettlementReferences home
    // (feature-design §8/§10, the rule-of-three cleanup bd babelstone-t7o3.18) — so the substrate settlement
    // leg and a family's embedded debit leg derive the IDENTICAL external_reference for the same process id
    // (the cross-saga no-double-debit invariant is structural, not a pair of literals that agree). NOT minted.

    /// <summary>
    /// Build the full typed payload for <paramref name="commandType"/>, or null if there is no recipe for it
    /// (the caller surfaces that as a fail-closed wiring error). PURE and byte-stable: no clock, no GUID
    /// minting (ADR-PC-010 §P5) — every reference is a deterministic function of the process id.
    /// </summary>
    /// <param name="commandType">The command NAME the state machine decided (a
    /// <see cref="SettlementProcess"/> command-name constant).</param>
    /// <param name="processId">The saga instance the command belongs to.</param>
    /// <param name="causationMessageId">The triggering event's message id (the §P7 causation reference) — a
    /// pre-existing id carried through, never minted here.</param>
    /// <param name="correlationId">The originating request's correlation reference, carried unchanged.</param>
    public static SettlementCommandPayload? Build(
        string commandType,
        Guid processId,
        Guid causationMessageId,
        Guid? correlationId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(commandType);

        return commandType switch
        {
            SettlementProcess.ReserveAccountBalance => new ReserveAccountBalanceCommand
            {
                ProcessId = processId,
                CausationMessageId = causationMessageId,
                CorrelationId = correlationId,
                AccountRef = SettlementReferences.Derive(SettlementReferences.AccountPrefix, processId),
                ReservationRef = SettlementReferences.Derive(SettlementReferences.ReservationPrefix, processId),
            },
            SettlementProcess.ConfirmDebit => new ConfirmDebitCommand
            {
                ProcessId = processId,
                CausationMessageId = causationMessageId,
                CorrelationId = correlationId,
                CoreHoldRef = SettlementReferences.Derive(SettlementReferences.CoreHoldPrefix, processId),
            },
            SettlementProcess.ConfirmCredit => new ConfirmCreditCommand
            {
                ProcessId = processId,
                CausationMessageId = causationMessageId,
                CorrelationId = correlationId,
                AccountRef = SettlementReferences.Derive(SettlementReferences.AccountPrefix, processId),
                CreditRef = SettlementReferences.Derive(SettlementReferences.CreditPrefix, processId),
            },
            SettlementProcess.QueryCoreDebitStatus => new QueryCoreDebitStatusCommand
            {
                ProcessId = processId,
                CausationMessageId = causationMessageId,
                CorrelationId = correlationId,
                // The SAME derived hold reference the indeterminate ConfirmDebit used (deterministic, not
                // minted), so the clearance query resolves exactly that in-flight operation.
                CoreHoldRef = SettlementReferences.Derive(SettlementReferences.CoreHoldPrefix, processId),
            },
            SettlementProcess.QueryCoreCreditStatus => new QueryCoreCreditStatusCommand
            {
                ProcessId = processId,
                CausationMessageId = causationMessageId,
                CorrelationId = correlationId,
                CreditRef = SettlementReferences.Derive(SettlementReferences.CreditPrefix, processId),
            },
            _ => null,
        };
    }
}
