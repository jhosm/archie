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
    string ProductConfigVersion = "",
    // The constitution's money movement(s) recorded APPEND-FIRST (ADR-PC-032 slot 5, bd babelstone-t7o3.13).
    // FRESH constitution carries NONE (null): its principal debit is the CONSTITUTION saga's gated step
    // (bd babelstone-t7o3.4, ADR-PC-029 slot 2 — a separate gating mechanism, not this Movement seam). A
    // RENEWAL constitution (DecideRenewalConstitution) carries ONE Originated Debit Movement against the
    // funding account — the matured principal re-debited into the renewed deposit (operation RolloverDebit) —
    // so the substrate-owned settlement saga effects the rollover debit off the promoted headers instead of
    // an eager in-engine settle. Optional/additive (defaulted null) so the fresh path and pre-Movement streams
    // declare no settlement header and start no settlement saga (forward-only, ADR-IC-002).
    IReadOnlyList<Movement>? Movements = null) : DomainEvent
{
    /// <summary>
    /// The settlement COUNTERPARTY a RENEWAL constitution's rollover-debit leg settles against
    /// (ADR-PC-043). NOT an Avro-mapped field: it is an init-only routing signal OUTSIDE the primary constructor,
    /// so the Avro codec (which maps only the primary-constructor parameters) never serializes it — this is a
    /// pure PRODUCER-side decision the family stamps at emission, promoted into the <c>ce_settlementtarget</c>
    /// header by <see cref="IntegrationHeaders"/>, never carried on the wire and irrelevant on replay/fold.
    /// Defaults to <see cref="SettlementTarget.LegacyDda"/> — the legacy demand core over the ACL (ADR-PC-016),
    /// so a family that has not opted a leg into engine-CA settlement keeps legacy routing UNCHANGED; the
    /// term-deposit service sets it to <see cref="SettlementTarget.EngineCa"/> for an engine-CA-targeted leg.
    /// </summary>
    public SettlementTarget SettlementTarget { get; init; } = SettlementTarget.LegacyDda;

    /// <summary>
    /// Promote a RENEWAL constitution's rollover-debit Movement origin/direction to the
    /// <c>ce_movementorigin</c> / <c>ce_movementdirections</c> CloudEvents extension headers the
    /// substrate-owned settlement saga auto-starts on (ADR-PC-032; ADR-IC-018), plus — when the
    /// leg settles against the engine-owned current account — the <c>ce_settlementtarget = engine-ca</c>
    /// counterparty header (ADR-PC-043), via the GENERIC counterparty-aware engine-spine seam
    /// (<see cref="MovementHeaders.ForOriginatedMovements(System.Collections.Generic.IReadOnlyList{Babelstone.Engine.Movement}, Babelstone.Engine.SettlementTarget)"/>). The routing header is HEADER-ONLY:
    /// the substrate keys the counterparty on it alone and never reads <see cref="Movement.AccountRef"/> from
    /// the body (ADR-IC-018). A <see cref="SettlementTarget.LegacyDda"/> leg promotes no target header, so
    /// its shape is byte-identical to the no-target seam (legacy routing UNCHANGED). A FRESH constitution
    /// carries no Movement, so it declares no settlement header and starts no settlement saga — its principal
    /// debit stays the CONSTITUTION saga's gated step, untouched by this seam.
    /// </summary>
    public override IReadOnlyDictionary<string, string>? IntegrationHeaders =>
        MovementHeaders.ForOriginatedMovements(Movements ?? [], SettlementTarget);

    // Constitution is a snapshot lifecycle boundary (ADR-PC-003 §P2 / event-store §8.1): the instance's
    // state is interpretable on its own here, so a snapshot is taken regardless of the per-N count.
    public override bool IsLifecycleBoundary => true;
}

/// <summary>Interest accrued for the period. For AT_MATURITY this is the single flow at
/// maturity: <c>GrossInterest = Accrual.SimpleInterest(principal, tan, DayCount.Between(start, maturity, Act360))</c>.</summary>
public sealed record InterestAccrued(Money GrossInterest, DateOnly AsOf) : DomainEvent;

/// <summary>Withholding tax applied flow-by-flow to the gross interest:
/// <c>Withholding.Withhold(gross, 2800) → (Tax, Net)</c>, with <c>Net = Gross − Tax</c> conserved to the cent.
/// <para><paramref name="WithheldOn"/> DATES the flow — for AT_MATURITY the maturity date, for early
/// termination the termination date — so the withholding ledger can be sliced per tax year on the read
/// surface (ADR-PC-027, ADR-IC-019 §D3, bd babelstone-60n8.8). STORE-ONLY and purely additive: there is no
/// WithholdingApplied.avsc, so this event is never bus-published, and the field is null-defaulted so a
/// pre-field stream replays byte-for-byte unchanged (forward-only, ADR-IC-002). A structural date, never
/// PII (ADR-PC-004 §P2).</para></summary>
public sealed record WithholdingApplied(Money Tax, Money Net, DateOnly WithheldOn = default) : DomainEvent;

/// <summary>The deposit matures and pays out: <c>TotalPayout = Principal + NetInterest</c>.
/// <para><paramref name="AutoRenewalPolicy"/> carries the deposit's renewal policy
/// (<c>NONE</c>/<c>SAME_TERM_CURRENT_RATE</c>/<c>SAME_TERM_SAME_RATE</c>), folded from
/// <c>DepositConstituted</c>, so a header-only consumer can route on it without decoding the
/// payload. Defaulted to <c>""</c> for backward compatibility with pre-field streams (the Avro
/// <c>auto_renewal_policy</c> field is the nullable, null-defaulted union, ADR-IC-002 §P2). A
/// structural enum token, never PII (ADR-PC-004 §P2).</para></summary>
/// <param name="Movements">The maturity payout's money movement(s) recorded APPEND-FIRST on this event
/// (ADR-PC-032 slot 5 / feature-design money-movement-settlement §7; bd babelstone-t7o3.13). A maturity
/// records ONE <see cref="MovementOrigin.Originated"/> <see cref="Movement"/>: a
/// <see cref="SettlementDirection.Credit"/> against the depositor's named payout account (the payout ENTERS
/// that account), operation <see cref="MovementOperation.PayMaturity"/>. The credit is CONFIRMATION-gated, not
/// funds-gated (ADR-PC-016 slot 5). The engine no longer settles eagerly on this path; the substrate-owned
/// settlement saga auto-starts off the promoted <c>ce_movementorigin</c> / <c>ce_movementdirections</c> headers
/// to effect the cash leg, gated. Optional/additive (defaulted empty) so pre-Movement streams still replay
/// (forward-only, ADR-IC-002): an empty carrier declares no settlement headers and starts no saga.</param>
public sealed record DepositMatured(
    Money PrincipalReturned,
    Money NetInterestPaid,
    Money TotalPayout,
    DateOnly MaturedOn,
    string AutoRenewalPolicy = "",
    IReadOnlyList<Movement>? Movements = null) : DomainEvent
{
    /// <summary>
    /// The settlement COUNTERPARTY the maturity payout's Credit leg settles against (ADR-PC-043). NOT
    /// an Avro-mapped field: it is an init-only routing signal OUTSIDE the primary constructor, so the Avro
    /// codec (which maps only the primary-constructor parameters) never serializes it — a pure PRODUCER-side
    /// decision the family stamps at emission, promoted into the <c>ce_settlementtarget</c> header by
    /// <see cref="IntegrationHeaders"/>, never carried on the wire and irrelevant on replay/fold. Defaults to
    /// <see cref="SettlementTarget.LegacyDda"/> (the legacy demand core over the ACL, ADR-PC-016), so legacy
    /// routing stays UNCHANGED unless the term-deposit service sets <see cref="SettlementTarget.EngineCa"/> for
    /// an engine-CA-targeted payout account.
    /// </summary>
    public SettlementTarget SettlementTarget { get; init; } = SettlementTarget.LegacyDda;

    /// <summary>
    /// Declares the maturity payout's Movement origin/direction (<c>movementorigin</c> /
    /// <c>movementdirections</c>, ADR-PC-032) — plus, when the payout settles against the engine-owned
    /// current account, the <c>ce_settlementtarget = engine-ca</c> counterparty header (ADR-PC-043) —
    /// AND the renewal policy (<c>autorenewalpolicy</c>) as CloudEvents extension attributes (ADR-IC-018),
    /// which the outbox relay promotes to the <c>ce_*</c> headers. The substrate-owned settlement saga
    /// auto-starts on <c>movementorigin == Originated</c> and, when present, diverts
    /// the counterparty HEADER-ONLY on <c>ce_settlementtarget</c> — never reading <see cref="Movement.AccountRef"/>
    /// from the body (ADR-IC-018); a renewal saga still filters header-only on <c>autorenewalpolicy</c>.
    /// The producers COMPOSE on one hop — distinct keys, no double-populate. Movement + target headers come via
    /// the GENERIC counterparty-aware engine-spine seam (<see cref="MovementHeaders.ForOriginatedMovements(System.Collections.Generic.IReadOnlyList{Babelstone.Engine.Movement}, Babelstone.Engine.SettlementTarget)"/>),
    /// so they name no family. Each header is emitted only when present: a movement-free / empty-policy event
    /// declares the corresponding header(s) only when it has them, and a <see cref="SettlementTarget.LegacyDda"/>
    /// leg promotes no target header (legacy routing UNCHANGED). All values are closed-enum / structural tokens,
    /// never PII (ADR-PC-004).
    /// </summary>
    public override IReadOnlyDictionary<string, string>? IntegrationHeaders
    {
        get
        {
            var movementHeaders = MovementHeaders.ForOriginatedMovements(Movements ?? [], SettlementTarget);
            var hasPolicy = !string.IsNullOrEmpty(AutoRenewalPolicy);
            if (movementHeaders is null && !hasPolicy)
            {
                return null;
            }

            var headers = new Dictionary<string, string>(StringComparer.Ordinal);
            if (movementHeaders is not null)
            {
                foreach (var kv in movementHeaders)
                {
                    headers[kv.Key] = kv.Value;
                }
            }

            if (hasPolicy)
            {
                headers["autorenewalpolicy"] = AutoRenewalPolicy;
            }

            return headers;
        }
    }

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
/// <param name="Movements">The coupon (or ADVANCE upfront-interest) payout's money movement(s) recorded
/// APPEND-FIRST on this event (ADR-PC-032 slot 5; bd babelstone-t7o3.13). A coupon records ONE
/// <see cref="MovementOrigin.Originated"/> <see cref="Movement"/>: a <see cref="SettlementDirection.Credit"/>
/// against the depositor's named payout account (the net coupon ENTERS that account), operation
/// <see cref="MovementOperation.PayCoupon"/>. The credit is CONFIRMATION-gated, not funds-gated (ADR-PC-016
/// slot 5). The engine no longer settles eagerly on this path; the substrate-owned settlement saga
/// auto-starts off the promoted <c>ce_movementorigin</c> / <c>ce_movementdirections</c> headers to effect the
/// cash leg, gated. Optional/additive (defaulted empty) so pre-Movement streams still replay (forward-only,
/// ADR-IC-002): an empty carrier declares no settlement headers and starts no saga.</param>
public sealed record InterestPaid(
    Guid DepositId,
    Money GrossInterest,
    Money WithholdingTax,
    Money NetInterest,
    DateOnly PaidOn,
    IReadOnlyList<Movement>? Movements = null) : DomainEvent
{
    /// <summary>
    /// The settlement COUNTERPARTY the coupon (or ADVANCE upfront-interest) Credit leg settles against
    /// (ADR-PC-043). NOT an Avro-mapped field: it is an init-only routing signal OUTSIDE the primary
    /// constructor, so the Avro codec (which maps only the primary-constructor parameters) never serializes it
    /// — a pure PRODUCER-side decision the family stamps at emission, promoted into the <c>ce_settlementtarget</c>
    /// header by <see cref="IntegrationHeaders"/>, never carried on the wire and irrelevant on replay/fold.
    /// Defaults to <see cref="SettlementTarget.LegacyDda"/> (the legacy demand core over the ACL, ADR-PC-016),
    /// so legacy routing stays UNCHANGED unless the term-deposit service sets <see cref="SettlementTarget.EngineCa"/>
    /// for an engine-CA-targeted payout/funding account.
    /// </summary>
    public SettlementTarget SettlementTarget { get; init; } = SettlementTarget.LegacyDda;

    /// <summary>
    /// Promote the coupon Movement's origin/direction to the <c>ce_movementorigin</c> /
    /// <c>ce_movementdirections</c> CloudEvents extension headers the substrate-owned settlement saga
    /// auto-starts on (ADR-PC-032; ADR-IC-018) — plus, when the coupon settles against the
    /// engine-owned current account, the <c>ce_settlementtarget = engine-ca</c> counterparty header
    /// (ADR-PC-043) — via the GENERIC counterparty-aware engine-spine seam
    /// (<see cref="MovementHeaders.ForOriginatedMovements(System.Collections.Generic.IReadOnlyList{Babelstone.Engine.Movement}, Babelstone.Engine.SettlementTarget)"/>). The routing header is HEADER-ONLY:
    /// the substrate keys the counterparty on it alone and never reads <see cref="Movement.AccountRef"/> from
    /// the body (ADR-IC-018); a <see cref="SettlementTarget.LegacyDda"/> leg promotes no target header
    /// (legacy routing UNCHANGED). Null/empty movements declare no settlement header, starting no saga.
    /// </summary>
    public override IReadOnlyDictionary<string, string>? IntegrationHeaders =>
        MovementHeaders.ForOriginatedMovements(Movements ?? [], SettlementTarget);
}

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
/// <param name="Movements">The early-termination payout's money movement(s) recorded APPEND-FIRST on this
/// event (ADR-PC-032 slot 5; bd babelstone-t7o3.13 relocates the leg, bd babelstone-t7o3.13.1 adds the
/// endpoint). An early termination records ONE <see cref="MovementOrigin.Originated"/>
/// <see cref="Movement"/>: a <see cref="SettlementDirection.Credit"/> against the depositor's named payout
/// account (the net settlement ENTERS that account), operation
/// <see cref="MovementOperation.PayEarlyTermination"/>. The credit is CONFIRMATION-gated, not funds-gated
/// (ADR-PC-016 slot 5). The engine no longer settles eagerly on this path; the substrate-owned settlement saga
/// auto-starts off the promoted <c>ce_movementorigin</c> / <c>ce_movementdirections</c> headers to effect the
/// cash leg, gated. Optional/additive (defaulted empty) so pre-Movement streams still replay (forward-only,
/// ADR-IC-002): an empty carrier declares no settlement headers and starts no saga.</param>
public sealed record DepositTerminatedEarly(
    Guid DepositId,
    Money PrincipalReturned,
    Money PenaltyAmount,
    Money NetSettlementAmount,
    DateOnly TerminatedOn,
    string TerminationReason,
    IReadOnlyList<Movement>? Movements = null) : DomainEvent
{
    /// <summary>
    /// The settlement COUNTERPARTY the early-termination payout's Credit leg settles against
    /// (ADR-PC-043). NOT an Avro-mapped field: it is an init-only routing signal OUTSIDE the primary constructor,
    /// so the Avro codec (which maps only the primary-constructor parameters) never serializes it — a pure
    /// PRODUCER-side decision the family stamps at emission, promoted into the <c>ce_settlementtarget</c>
    /// header by <see cref="IntegrationHeaders"/>, never carried on the wire and irrelevant on replay/fold.
    /// Defaults to <see cref="SettlementTarget.LegacyDda"/> (the legacy demand core over the ACL, ADR-PC-016),
    /// so legacy routing stays UNCHANGED unless the term-deposit service sets <see cref="SettlementTarget.EngineCa"/>
    /// for an engine-CA-targeted payout account.
    /// </summary>
    public SettlementTarget SettlementTarget { get; init; } = SettlementTarget.LegacyDda;

    /// <summary>
    /// Promote the early-termination payout Movement's origin/direction to the <c>ce_movementorigin</c> /
    /// <c>ce_movementdirections</c> CloudEvents extension headers the substrate-owned settlement saga
    /// auto-starts on (ADR-PC-032; ADR-IC-018) — plus, when the payout settles against the
    /// engine-owned current account, the <c>ce_settlementtarget = engine-ca</c> counterparty header
    /// (ADR-PC-043) — via the GENERIC counterparty-aware engine-spine seam
    /// (<see cref="MovementHeaders.ForOriginatedMovements(System.Collections.Generic.IReadOnlyList{Babelstone.Engine.Movement}, Babelstone.Engine.SettlementTarget)"/>). The routing header is HEADER-ONLY:
    /// the substrate keys the counterparty on it alone and never reads <see cref="Movement.AccountRef"/> from
    /// the body (ADR-IC-018); a <see cref="SettlementTarget.LegacyDda"/> leg promotes no target header
    /// (legacy routing UNCHANGED). Null/empty movements declare no settlement header, starting no saga.
    /// </summary>
    public override IReadOnlyDictionary<string, string>? IntegrationHeaders =>
        MovementHeaders.ForOriginatedMovements(Movements ?? [], SettlementTarget);

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

/// <summary>A correction to a previously-recorded STRUCTURAL fact, carrying the corrected VALUE inline as
/// a typed field (bd babelstone-j7mm.2; ADR-PC-002 §P2 *Revised 2026-06-27*). Exactly ONE typed
/// corrected-value field is set — the one named by <paramref name="CorrectedField"/> — and the pure fold
/// substitutes it into the deposit position, so a query reads back the CORRECTED value, not just a bumped
/// counter. Structural fields ONLY (principal as Money cents, rate as basis points, dates): these are not
/// PII, so ADR-PC-004 §P2 does not bind them (the same typed-field precedent as
/// <see cref="DepositPartiallyWithdrawn"/>'s <c>RemainingPrincipal</c>). <paramref name="EffectiveFrom"/>
/// is the valid-time that feeds the ADR-PC-002 §P2 bitemporal supersession.</summary>
/// <param name="CorrectedField">The structural field being corrected (e.g. <c>principal</c>, <c>rate</c>,
/// <c>start_date</c>, <c>maturity_date</c>) — a stable name, never a value. The decider rejects an
/// unknown field before any append (ADR-PC-002 §P2 *Revised 2026-06-27*).</param>
/// <param name="CorrectedPrincipal">The corrected principal (Money cents) when
/// <paramref name="CorrectedField"/> is <c>principal</c>; null otherwise. A structural amount, NOT PII.</param>
/// <param name="CorrectedTanBasisPoints">The corrected nominal annual rate in basis points when
/// <paramref name="CorrectedField"/> is <c>rate</c>; null otherwise.</param>
/// <param name="CorrectedStartDate">The corrected value-date the deposit started when
/// <paramref name="CorrectedField"/> is <c>start_date</c>; null otherwise.</param>
/// <param name="CorrectedMaturityDate">The corrected maturity date when
/// <paramref name="CorrectedField"/> is <c>maturity_date</c>; null otherwise.</param>
public sealed record DepositCorrected(
    Guid DepositId,
    string CorrectionId,
    string CorrectedField,
    Money? CorrectedPrincipal,
    int? CorrectedTanBasisPoints,
    DateOnly? CorrectedStartDate,
    DateOnly? CorrectedMaturityDate,
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

/// <summary>The maturity payout could not be delivered to the beneficiary account, so the deposit is
/// held PAYOUT-PENDING at source rather than disgorged (ADR-PC-043 slot 5). In
/// plain English: the deposit matured but the money had nowhere to land — the beneficiary account is
/// closed, dormant-past-revival, or does not exist — so instead of losing the payout the deposit KEEPS
/// the funds and marks itself payout-pending. The lifecycle-driver's re-attempt rule
/// (<c>PayoutPendingRetryRule</c>) watches this flag and re-fires the payout once a live destination
/// exists (the re-attempt lands exactly once, keyed on the same economic intent). This is a
/// NON-terminal, reversible marker on the CLOSED-side of maturity: <see cref="DepositLifecycle.Matured"/>
/// → <see cref="DepositLifecycle.PayoutPending"/>, resolved back out when the credit lands.</summary>
/// <remarks>STORE-ONLY (ADR-IC-017 — an internal source-side hold marker, not a bus event; the
/// undeliverable credit's attributed IOU is the engine's cross-cutting <c>operations.CreditUnapplied</c>).
/// STRUCTURAL only, no PII (ADR-PC-004): opaque refs, a machine reason code, and an input date.</remarks>
/// <param name="DepositId">The deposit stream held payout-pending — never PII (ADR-PC-004).</param>
/// <param name="BeneficiaryAccountRef">The opaque beneficiary account the payout could not reach — a
/// reference the engine resolves internally, never PII (ADR-PC-004).</param>
/// <param name="Reason">The stable machine reason the payout was undeliverable (e.g.
/// <c>BENEFICIARY_ACCOUNT_CLOSED</c>) — never free-text PII.</param>
/// <param name="PendingSince">The economic date the payout became pending — an input date, never a clock
/// read in a fold (ADR-PC-023).</param>
public sealed record DepositPayoutHeld(
    Guid DepositId,
    string BeneficiaryAccountRef,
    string Reason,
    DateOnly PendingSince) : DomainEvent;

/// <summary>The held maturity payout was delivered once a live destination existed, so the deposit
/// leaves payout-pending and reaches its settled maturity terminal (ADR-PC-043 slot 5).
/// In plain English: the account that could not receive the payout became receivable
/// again, the re-attempt landed the credit, and the deposit is finally, cleanly matured-and-paid. This is
/// the resolve leg of the reversible payout-pending marker: <see cref="DepositLifecycle.PayoutPending"/>
/// → <see cref="DepositLifecycle.Matured"/>.</summary>
/// <remarks>STORE-ONLY (ADR-IC-017). STRUCTURAL only, no PII (ADR-PC-004).</remarks>
/// <param name="DepositId">The deposit stream whose payout landed — never PII (ADR-PC-004).</param>
/// <param name="BeneficiaryAccountRef">The opaque account the payout finally landed on — never PII (ADR-PC-004).</param>
/// <param name="LandedOn">The economic date the payout landed — an input date, never a clock read.</param>
public sealed record DepositPayoutLanded(
    Guid DepositId,
    string BeneficiaryAccountRef,
    DateOnly LandedOn) : DomainEvent
{
    // Landing the held payout is a closing snapshot lifecycle boundary (ADR-PC-003 §P2) — the deposit
    // reaches its settled maturity terminal, interpretable on its own.
    public override bool IsLifecycleBoundary => true;
}
