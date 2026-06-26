namespace Babelstone.Orchestrator.Saga.Settlement;

/// <summary>
/// The ONE home for the settlement legs' DETERMINISTIC, process-id-derived references — the reservation /
/// Core-hold / Core-txn / credit tokens every cash leg names (ADR-PC-032; feature-design
/// money-movement-settlement §8/§10, the rule-of-three cleanup of bd babelstone-t7o3.18). In plain English:
/// when a saga places a hold or confirms a debit, it has to name "which hold" / "which debit" with a stable
/// token the Core ACL recognises on a retry. That token is just a fixed prefix plus the saga's process id —
/// and the SAME derivation has to be used everywhere a cash leg is composed, so a constitution debit and the
/// substrate settlement leg for the SAME process derive the IDENTICAL token. This collapses three verbatim
/// copies of that derivation (the substrate settlement factory, the term-deposit constitution factory, the
/// renewal factory) into one.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why it lives in the substrate (ADR-IC-018 §D2 — the family → substrate arrow).</b> The derived
/// reference is the <c>external_reference</c> half of the ACL's idempotency key (ADR-IC-012 §P4);
/// it names no family (it is a prefix + the opaque process id). Both the substrate-owned settlement saga and a
/// family's embedded debit leg compose it, so it belongs beside the substrate's settlement command surface,
/// not in any one family — the family depends on the substrate, never the reverse.
/// </para>
/// <para>
/// <b>The load-bearing cross-saga invariant.</b> The reference is STABLE across re-emission (a prefix + the
/// process id's hex, no minted GUID, no clock — ADR-PC-010 §P5), so a RETRY_PERMITTED reissue presents the
/// SAME token and the ACL dedups on it (no double-move). Crucially it is also stable across the constitution
/// debit leg and the substrate settlement leg: the constitution's <c>SagaCommandPayloadFactory</c> and the
/// substrate's <c>SettlementCommandPayloadFactory</c> now derive every shared reference through THIS helper,
/// so the same process id yields the same <c>RSV-</c> / <c>CORE-HOLD-</c> token in both — the cross-saga
/// no-double-debit guarantee is structural, not a pair of literals that happen to agree.
/// </para>
/// </remarks>
public static class SettlementReferences
{
    /// <summary>The reversible balance-hold reference prefix (the <c>ReserveAccountBalance</c> leg's
    /// reservation token + the <c>ReleaseBalanceReservation</c> compensation's target).</summary>
    public const string ReservationPrefix = "RSV-";

    /// <summary>The Core debit-hold reference prefix (the <c>ConfirmDebit</c> leg's hold token + the
    /// <c>QueryCoreDebitStatus</c> clearance's target — the external_reference the ACL keys the debit on).</summary>
    public const string CoreHoldPrefix = "CORE-HOLD-";

    /// <summary>The Core transaction reference prefix (the committed debit's txn token — the
    /// <c>ActivateDeposit</c> / <c>ReverseCoreDebit</c> legs' Core-txn reference).</summary>
    public const string CoreTxnPrefix = "CT-";

    /// <summary>The opaque account reference prefix the substrate's staged <c>account_ref</c> seam derives
    /// (until each family threads the real promoted <c>Movement.AccountRef</c>; ADR-PC-032 §A6).</summary>
    public const string AccountPrefix = "ACCT-";

    /// <summary>The credit reference prefix (the <c>ConfirmCredit</c> leg's credit token + the
    /// <c>QueryCoreCreditStatus</c> clearance's target — the external_reference the ACL keys the credit on).</summary>
    public const string CreditPrefix = "CREDIT-";

    /// <summary>
    /// Derive a DETERMINISTIC reference for one settlement leg: <paramref name="prefix"/> + the process id's
    /// hex (<c>"N"</c> format). Stable across re-emission AND across the sagas that compose the same leg, so
    /// the assembled command body is byte-stable (ADR-PC-010 §P5) and the ACL's idempotency dedups on it (the
    /// reserve and the release, the confirm and the clearance, the constitution debit and the settlement
    /// debit — all derive the SAME token for the same process id). NEVER a minted GUID, NEVER a wall clock.
    /// </summary>
    public static string Derive(string prefix, Guid processId)
    {
        ArgumentException.ThrowIfNullOrEmpty(prefix);
        return prefix + processId.ToString("N");
    }
}
