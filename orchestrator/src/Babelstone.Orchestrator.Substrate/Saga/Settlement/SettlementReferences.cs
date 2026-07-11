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

    /// <summary>The opaque account reference prefix the substrate's <c>account_ref</c> seam derives as the
    /// FALLBACK when no real account_ref is threaded onto the settlement intent. The
    /// engine-CA leg now carries the customer's REAL promoted <c>Movement.AccountRef</c> as the destination
    /// (ADR-PC-043; <c>SettlementIntent.AccountRef</c>), which the
    /// <c>SettlementCommandPayloadFactory</c> forwards untouched; this <c>ACCT-{processId}</c> placeholder
    /// remains only for the legacy-DDA path and the pre-promotion platform default (where the legacy core
    /// resolves the account from the process-scoped business reference) — see
    /// <c>SettlementCommandPayloadFactory.cs</c> <c>&lt;remarks&gt;</c>.</summary>
    public const string AccountPrefix = "ACCT-";

    /// <summary>The credit reference prefix (the <c>ConfirmCredit</c> leg's credit token + the
    /// <c>QueryCoreCreditStatus</c> clearance's target — the external_reference the ACL keys the credit on).</summary>
    public const string CreditPrefix = "CREDIT-";

    /// <summary>The economic-intent reference prefix (the ADR-PC-043 slot-4 exactly-once key). The engine-CA
    /// settlement leg's append <c>command_id</c> is derived from THIS token, NOT the HTTP Idempotency-Key — the
    /// deliberate, single-owner slot-4 inversion of ADR-PC-029/ADR-PC-032, legitimate because the engine owns
    /// both sides of the CA contract.</summary>
    public const string IntentPrefix = "INTENT-";

    /// <summary>The resolution-intent reference prefix (ADR-PC-043 §Idempotency). An operator re-target / retry
    /// of an undeliverable credit derives its <c>ResolutionIntentId</c> from the SAME <see cref="IntentPrefix"/>
    /// token — never fresh — so a late original apply and the resolution collapse to exactly one landing.</summary>
    public const string ResolutionPrefix = "RESOLVE-";

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

    /// <summary>
    /// Derive the ADR-PC-043 slot-4 economic-intent id <c>IntentId = f(source_id, occurrence)</c> — the
    /// PER-PAYOUT exactly-once key (e.g. <c>f(deposit_id, "maturity")</c>, <c>f(loan_id, "installment-3")</c>),
    /// distinct from the per-occurrence saga <c>process_id</c>. In plain English: it names WHICH economic
    /// event this payout is, from the source aggregate and its occurrence, so a saga reissue (byte-identical
    /// body, fresh dispatch <c>message_id</c>) and a re-route both derive the SAME intent — the CA-apply
    /// <c>command_id</c> the settlement reference carries then collapses at <c>command_dedup</c> to one append.
    /// DETERMINISTIC (ADR-PC-010 §P5 — no clock, no mint): the same <paramref name="sourceId"/> +
    /// <paramref name="occurrence"/> always yield the same intent id.
    /// </summary>
    /// <param name="sourceId">The source aggregate the payout belongs to (the deposit / loan). A structural
    /// reference, never PII (ADR-PC-004 §P2).</param>
    /// <param name="occurrence">The stable occurrence key on that source (<c>"maturity"</c>,
    /// <c>"installment-3"</c>, <c>"coupon-2"</c>) — the source-family payout's <c>LifecycleCommandKey</c>
    /// occurrence, never a wall-clock date or a fresh value (ADR-PC-036).</param>
    public static string DeriveIntentId(Guid sourceId, string occurrence)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(occurrence);
        return IntentPrefix + sourceId.ToString("N") + "|" + occurrence;
    }

    /// <summary>
    /// Derive a settlement reference (the CA-apply <c>command_id</c> the body carries) from the economic-INTENT
    /// id, NOT the process id — the ADR-PC-043 slot-4 rule. <paramref name="prefix"/> namespaces the leg
    /// (credit / debit) and the intent id is the exactly-once axis, so the debit and the credit for the same
    /// intent are distinct references while a reissue of EITHER leg for the same intent presents the identical
    /// token. Byte-stable (ADR-PC-010 §P5); NEVER a minted GUID, NEVER the HTTP Idempotency-Key.
    /// </summary>
    /// <param name="prefix">The leg-namespacing prefix (<see cref="CreditPrefix"/> / <see cref="CoreHoldPrefix"/>).</param>
    /// <param name="intentId">The economic-intent id from <see cref="DeriveIntentId"/>.</param>
    public static string DeriveFromIntent(string prefix, string intentId)
    {
        ArgumentException.ThrowIfNullOrEmpty(prefix);
        ArgumentException.ThrowIfNullOrWhiteSpace(intentId);
        return prefix + intentId;
    }

    /// <summary>
    /// Derive the RESOLUTION-intent reference for an undeliverable-credit re-target / retry (ADR-PC-043
    /// §Idempotency): <c>ResolutionIntentId = g(IntentId)</c>, derived from the SAME original intent id, never
    /// fresh. So a late original apply and the operator resolution collapse to exactly one landing by
    /// construction, and a second <c>CreditReapplied</c> for a resolved intent is a reconciliation signal, not
    /// a double-pay. DETERMINISTIC and byte-stable (ADR-PC-010 §P5).
    /// </summary>
    /// <param name="intentId">The ORIGINAL economic-intent id from <see cref="DeriveIntentId"/> — the resolution
    /// key is a pure function of it, so a fresh id fails the intent-derived check (the double-pay guard).</param>
    public static string DeriveResolutionIntentId(string intentId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(intentId);
        return ResolutionPrefix + intentId;
    }
}
