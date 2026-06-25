using Babelstone.Engine;
using Babelstone.FinancialTypes;

namespace Babelstone.Families.TermDeposit;

/// <summary>
/// A commercial-eligibility verdict recorded on a constitution event for AUDIT LINEAGE only
/// (ADR-PC-024 §1): the opaque <c>{ satisfied, evidence_ref, evaluated_at }</c> triple an upstream
/// authority resolved for one predicate, which the saga gathered and the decider stamps onto
/// <see cref="DepositConstituted"/> / <see cref="DepositConstitutionFailed"/>. STRUCTURAL, not PII:
/// <see cref="EvidenceRef"/> is a resolvable reference, never identity data (ADR-PC-004 §P2,
/// ADR-PC-024 §1). The engine never re-evaluates a verdict; this record is a lineage artefact, not a
/// decision input on replay (the refusal is re-derived from the COMMAND's verdicts, ADR-PC-024 §4).
/// </summary>
/// <param name="Key">The engine-owned closed verdict key, e.g. <c>is_new_money</c> (ADR-PC-024 §1).</param>
/// <param name="Satisfied">Whether the upstream authority found the predicate satisfied.</param>
/// <param name="EvidenceRef">An opaque reference to the upstream evidence — NOT identity data.</param>
/// <param name="EvaluatedAt">When the upstream authority took the verdict (audit lineage / freshness).</param>
public sealed record RecordedPreconditionVerdict(
    string Key,
    bool Satisfied,
    string EvidenceRef,
    DateTimeOffset EvaluatedAt);

// The four AT_MATURITY term-deposit events (E.1, archie-uqlm). Each carries the
// already-COMPUTED facts as Money — the financial-math kernel runs on the command/decider
// side (E.3) that builds these, never inside a handler fold (handlers stay pure, BENG001/2/3).
// Events are STRUCTURAL: no depositor PII (name/NIF) travels here (ADR-PC-004 §P2). The
// pack/schema/family pins (pt.2026.1 / term_deposit@2026.1) ride on the EventEnvelope via
// AppendContext, not on the event records.

/// <summary>The deposit is opened: principal, the rate-sheet-resolved TAN, and the
/// AT_MATURITY schedule are fixed at constitution.</summary>
/// <param name="TanBasisPoints">Annual nominal rate (TAN) in basis points, resolved from the
/// rate sheet at constitution (ADR-PC-008 §P3) — never inline config.</param>
/// <param name="RateSheetVersionId">The rate-sheet version the TAN was resolved from (pinned).</param>
/// <param name="PaymentPeriodMonths">The coupon cadence in months for the PERIODIC variant —
/// 1 (monthly) or 3 (quarterly), the only cadences v1 prices (02 §2.1). Carried so the engine
/// can derive each coupon window from this event alone. It is 0 for AT_MATURITY and ADVANCE,
/// which have no coupons. Optional/additive (defaulted) so pre-F.1 AT_MATURITY streams that
/// never carried it still replay (forward-only schema evolution).</param>
/// <param name="ProductCode">The catalogue product code (e.g. <c>dpz_pt_12m_juros_venc</c>) — the
/// STRUCTURAL product identifier the rate sheet prices (<c>ConstituteDepositCommand.ProductId</c>),
/// stamped by the decider so the D.4 read model can denormalize the queryable "which product is
/// this" dimension that <c>RateSheetVersionId</c> (a price/version key, one-to-many to products)
/// cannot provide. Structural, NOT PII (ADR-PC-004 §P2). Optional/additive (defaulted "") so
/// pre-v794 streams that never carried it still replay as the empty default (forward-only schema
/// evolution); those historical deposits are NOT back-fillable because the code is discarded at
/// constitution and the rate-sheet version is one-to-many to products. Prospective only
/// (bd babelstone-v794).</param>
/// <param name="Role">The pricing role the rate sheet priced the TAN against
/// (<c>ConstituteDepositCommand.Role</c>, e.g. <c>standard</c>) — a STRUCTURAL pricing dimension,
/// NOT PII (ADR-PC-004 §P2). Stamped by the decider and folded onto the position so the engine can
/// re-resolve the SAME <c>(product, role)</c> rate at auto-renewal entirely from the closing
/// deposit, keeping product-family knowledge out of the orchestrator (ADR-IC-003 §A7,
/// bd babelstone-mtto.5). Optional/additive (defaulted "") so pre-mtto.5 streams that never carried
/// it still replay as the empty default (forward-only schema evolution); a renewal of such a
/// pre-field deposit defaults the empty role to <c>standard</c> (the v1 default role).</param>
/// <param name="FundingAccount">An OPAQUE funding-account TOKEN — a REFERENCE the engine resolves
/// internally, NEVER an IBAN/cleartext account identifier (ADR-PC-004 §P2: references are allowed on
/// the durable bus; PII is not). The legacy current account the principal was debited
/// (<c>ConstituteDepositCommand.FundingAccount</c>). Stamped by the decider and folded onto the
/// position so the engine can settle the auto-renewal rollover debit against the SAME funding
/// reference entirely from the closing deposit, keeping product/funding knowledge out of the
/// orchestrator (ADR-IC-003 §A7, bd babelstone-mtto.5). Optional/additive (defaulted "") so
/// pre-mtto.5 streams that never carried it still replay as the empty default (forward-only schema
/// evolution); a renewal of such a pre-field deposit fails loud rather than debit an empty funding
/// reference.</param>
/// <remarks>
/// NOTE (ADR-PC-024 §1, F.9 bd babelstone-k6r8.2): for an ACCEPTED constitution the ADR also names
/// this event as a home for the resolved commercial-eligibility verdicts "for audit lineage only".
/// That lineage is NOT carried here in v1: <c>DepositConstituted</c> is a BUS-PUBLISHED event, and the
/// Avro bus codec (<c>AvroEventSerializer</c>) enforces strict C#↔.avsc parity AND has no array-of-record
/// support, so a verdict-list field would force the audit lineage onto the durable bus (widening the bus
/// contract for store-only audit data) and require a generic-codec change. Per ADR-PC-028 the audit book
/// of record is the JSON <c>events.payload</c>, not the Avro projection; the REFUSAL-path lineage that the
/// load-bearing commitment (<c>CONSTITUTION_PRECONDITION_REFUSAL</c>) cares about rides
/// <see cref="DepositConstitutionFailed.Preconditions"/> (store-only, no .avsc). Accepted-path
/// on-envelope lineage is deferred to v1.x — see ADR-PC-024 §1 Amendment (2026-06-12).
/// </remarks>
public sealed record DepositConstituted(
    Guid DepositId,
    Money Principal,
    int TanBasisPoints,
    string RateSheetVersionId,
    int TermDays,
    DateOnly StartDate,
    DateOnly MaturityDate,
    string InterestVariant,
    string AutoRenewalPolicy,
    int PaymentPeriodMonths = 0,
    string ProductCode = "",
    string Role = "",
    string FundingAccount = "",
    // The F.12 partial-withdrawal policy PINNED at constitution (bd k6r8.8/qze9): the three gates a
    // partial early withdrawal must clear, resolved from the product config and stamped here so a later
    // config edit cannot retroactively change a live deposit's withdrawal rights — the same per-instance
    // pinning the rate/term/variant get (ADR-PC-009). All three default to 0 (the Unrestricted policy),
    // additive with Avro defaults so pre-F.12 records still decode (forward-only evolution, ADR-IC-002).
    // Cents are `long` (not Money) to mirror PartialWithdrawalPolicy and keep the Avro field names
    // `min_withdrawal_cents` / `min_remaining_balance_cents` clean (no double `_cents` suffix).
    long MinWithdrawalCents = 0,
    long MinRemainingBalanceCents = 0,
    int LockupPeriodDays = 0,
    // The product-config generation this deposit was constituted under, PINNED per-event (ADR-PC-009 §A2):
    // a content-hash version (`sha256:<hex>`) the decider resolves from the product config in the SAME
    // constitution transaction (ADR-PC-008 §S2) and stamps here — a PAYLOAD-shaped pin, like
    // RateSheetVersionId, NOT an envelope/AppendContext column like pack_version. So a replay can prove
    // exactly which product-config generation governed the deposit (REPLAY_PIN_PER_EVENT). A structural
    // version string, NOT PII (ADR-PC-004 §P2). Additive with an Avro default "" so pre-pin records still
    // decode (forward-only evolution, ADR-IC-002 §P3) — empty when no product-config store is wired
    // (direct callers) or the config carried no version. Prospective only (bd babelstone-fk7m.9).
    string ProductConfigVersion = "") : DomainEvent
{
    // Constitution is a snapshot lifecycle boundary (ADR-PC-003 §P2 / event-store §8.1): the instance's
    // state is interpretable on its own here, so a snapshot is taken regardless of the per-N count.
    public override bool IsLifecycleBoundary => true;
}

/// <summary>Interest accrued for the period. For AT_MATURITY this is the single flow at
/// maturity: <c>GrossInterest = Accrual.SimpleInterest(principal, tan, DayCount.Between(start, maturity, Act360))</c>.</summary>
public sealed record InterestAccrued(Money GrossInterest, DateOnly AsOf) : DomainEvent;

/// <summary>Withholding tax applied flow-by-flow to the gross interest:
/// <c>Withholding.Withhold(gross, 2800) → (Tax, Net)</c>, with <c>Net = Gross − Tax</c> conserved to the cent.</summary>
public sealed record WithholdingApplied(Money Tax, Money Net) : DomainEvent;

/// <summary>The deposit matures and pays out: <c>TotalPayout = Principal + NetInterest</c>.
/// <para><paramref name="AutoRenewalPolicy"/> carries the deposit's renewal policy
/// (<c>NONE</c>/<c>SAME_TERM_CURRENT_RATE</c>/<c>SAME_TERM_SAME_RATE</c>), folded from
/// <c>DepositConstituted</c>, so a header-only consumer can route on it without decoding the
/// payload. Defaulted to <c>""</c> for backward compatibility with pre-field streams (the Avro
/// <c>auto_renewal_policy</c> field is the nullable, null-defaulted union, ADR-IC-002 §P2). A
/// structural enum token, never PII (ADR-PC-004 §P2).</para></summary>
public sealed record DepositMatured(
    Money PrincipalReturned,
    Money NetInterestPaid,
    Money TotalPayout,
    DateOnly MaturedOn,
    string AutoRenewalPolicy = "") : DomainEvent
{
    /// <summary>
    /// Declares the renewal policy as the <c>autorenewalpolicy</c> CloudEvents extension attribute
    /// (ADR-IC-018 §P5), which the outbox relay promotes to the <c>ce_autorenewalpolicy</c> header —
    /// letting a renewal saga filter header-only. Emitted ONLY when the policy is non-empty: an
    /// empty/absent policy (pre-field streams) declares no extension header, leaving the relay's
    /// standard CE header set untouched. The token is structural, not PII (ADR-PC-004 §P2).
    /// </summary>
    public override IReadOnlyDictionary<string, string>? IntegrationHeaders =>
        string.IsNullOrEmpty(AutoRenewalPolicy)
            ? null
            : new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["autorenewalpolicy"] = AutoRenewalPolicy,
            };

    // Maturity is a snapshot lifecycle boundary (ADR-PC-003 §P2 / event-store §8.1) — a closing point
    // where the instance's terminal state is interpretable on its own.
    public override bool IsLifecycleBoundary => true;
}

// The seven remaining term-deposit events (F.2, babelstone-5czr) — the full lifecycle
// beyond the AT_MATURITY happy path. Same discipline as the four above: each carries
// already-COMPUTED facts (Money cents-native, ADR-PC-010 §P1), is STRUCTURAL only
// (computed facts + opaque references; NO depositor/heir PII — name/NIF/IBAN — in
// cleartext OR ciphertext, ADR-PC-004 §P2), and is folded by a PURE handler. The
// lifecycle state machine and transition legality are F.3 (babelstone-29v8), NOT here;
// the bitemporal projection/correction is D.1/D.2 / F.6, NOT here.

/// <summary>Constitution was rejected by a config/rule check, so no deposit exists. Carries
/// failure CODES only: <paramref name="FailureReason"/> is the machine code and
/// <paramref name="FailureDetail"/> describes the offending config or rule — NEVER anything
/// about the customer (ADR-PC-004 §P2).</summary>
/// <param name="FailureReason">Stable failure code (e.g. <c>RATE_SHEET_NOT_FOUND</c>).</param>
/// <param name="FailureDetail">Human-readable detail about the config/rule that failed — never PII.</param>
/// <param name="Preconditions">For an <c>ELIGIBILITY_NOT_MET</c> refusal (ADR-PC-024 §5), the
/// commercial-eligibility verdicts the saga resolved upstream — recorded for AUDIT LINEAGE only
/// (ADR-PC-024 §1), each an opaque <c>{ satisfied, evidence_ref, evaluated_at }</c> triple,
/// STRUCTURAL not PII (ADR-PC-004 §P2). So the audit trail shows WHICH verdict drove the refusal and
/// on what (referenced) evidence, beyond the unmet-key names in <paramref name="FailureDetail"/>.
/// Optional/additive (defaulted empty) so non-eligibility failures (e.g. <c>RATE_SHEET_NOT_FOUND</c>)
/// and pre-F.9 streams carry none and still replay (forward-only, bd babelstone-k6r8.2).</param>
public sealed record DepositConstitutionFailed(
    Guid DepositId,
    string FailureReason,
    string FailureDetail,
    IReadOnlyList<RecordedPreconditionVerdict>? Preconditions = null) : DomainEvent;

/// <summary>Interest is paid out (the periodic/coupon variant, vs the single AT_MATURITY flow):
/// <c>NetInterest = GrossInterest − WithholdingTax</c> conserved to the cent.</summary>
public sealed record InterestPaid(
    Guid DepositId,
    Money GrossInterest,
    Money WithholdingTax,
    Money NetInterest,
    DateOnly PaidOn) : DomainEvent;

/// <summary>The deposit auto-renews into a new term: a fresh deposit (<paramref name="NewDepositId"/>)
/// is constituted from the rolled-over principal at the new rate-sheet-resolved TAN. The new
/// TAN/schedule are pinned facts (ADR-PC-008 §P3), resolved by the decider — never inline config.</summary>
public sealed record DepositRenewed(
    Guid DepositId,
    Guid NewDepositId,
    Money RolloverPrincipal,
    string NewRateSheetVersionId,
    int NewTanBasisPoints,
    int NewTermDays,
    DateOnly RenewalDate,
    DateOnly NewMaturityDate) : DomainEvent
{
    // Renewal is a snapshot lifecycle boundary (ADR-PC-003 §P2 / event-store §8.1).
    public override bool IsLifecycleBoundary => true;
}

/// <summary>The deposit is broken before maturity. The depositor's payout is the principal still on
/// deposit PLUS the net interest accrued over the elapsed period, less the penalty haircut:
/// <c>NetSettlementAmount = PrincipalReturned + NetAccruedInterest − PenaltyAmount</c> (the F.4 decider
/// settles the accrued NET interest back too — only the penalty is forfeited, not the accrued interest).
/// <paramref name="PenaltyAmount"/> is the EFFECTIVE penalty actually charged and is non-negative. The
/// gross accrued interest, withholding, and net payout are emitted as the paired
/// <see cref="InterestAccrued"/>/<see cref="WithholdingApplied"/> flows (02 §2.5).</summary>
public sealed record DepositTerminatedEarly(
    Guid DepositId,
    Money PrincipalReturned,
    Money PenaltyAmount,
    Money NetSettlementAmount,
    DateOnly TerminatedOn,
    string TerminationReason) : DomainEvent
{
    // Early termination is a snapshot lifecycle boundary (ADR-PC-003 §P2 / event-store §8.1).
    public override bool IsLifecycleBoundary => true;
}

/// <summary>A partial withdrawal reduces the deposit's principal:
/// <c>RemainingPrincipal</c> is the principal left after taking <paramref name="WithdrawnAmount"/> out.</summary>
public sealed record DepositPartiallyWithdrawn(
    Guid DepositId,
    Money WithdrawnAmount,
    Money RemainingPrincipal,
    DateOnly WithdrawnOn) : DomainEvent
{
    // Partial withdrawal is a snapshot lifecycle boundary (ADR-PC-003 §P2 / event-store §8.1).
    public override bool IsLifecycleBoundary => true;
}

/// <summary>A correction to a previously-recorded fact. Carries opaque REFERENCES only
/// (<paramref name="PreviousValueRef"/> / <paramref name="CorrectedValueRef"/> point at the
/// resolvable values) — no PII travels here (ADR-PC-004 §P2). <paramref name="EffectiveFrom"/>
/// is the valid-time that feeds the D.1 §P2 bitemporal supersession; the real read-model
/// correction is D.1/D.2, NOT this fold.</summary>
public sealed record DepositCorrected(
    Guid DepositId,
    string CorrectionId,
    string CorrectedField,
    string PreviousValueRef,
    string CorrectedValueRef,
    DateOnly EffectiveFrom,
    string CorrectionReason) : DomainEvent;

/// <summary>The deposit balance is transferred to a deceased holder's heirs (succession).</summary>
/// <remarks>
/// Carries NO heir PII — no name, NIF, or IBAN — only the opaque <paramref name="HeirCaseRef"/>
/// (the succession case reference). The engine resolves heir identity internally from that
/// reference (ADR-PC-004 §P2); no identity ever rides on this structural event, in cleartext
/// or ciphertext.
/// </remarks>
/// <param name="HeirCaseRef">Opaque reference to the succession case — NOT an heir identity.</param>
public sealed record DepositTransferredToHeirs(
    Guid DepositId,
    string HeirCaseRef,
    Money TransferredBalance,
    DateOnly TransferDate) : DomainEvent
{
    // Succession (transfer to heirs) is a closing snapshot lifecycle boundary (ADR-PC-003 §P2 /
    // event-store §8.1: "termination") — the terminal state is interpretable on its own.
    public override bool IsLifecycleBoundary => true;
}
