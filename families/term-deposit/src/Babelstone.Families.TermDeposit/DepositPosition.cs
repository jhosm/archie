using Babelstone.FinancialTypes;

namespace Babelstone.Families.TermDeposit;

/// <summary>The lifecycle states the term-deposit aggregate folds into. F.2 (babelstone-5czr)
/// adds the full event set's terminal/transition labels; the transition LEGALITY (which
/// states may move to which) is the F.3 state machine (babelstone-29v8), deliberately NOT
/// enforced here — these handlers are pure folds that label state, not guards.</summary>
public enum DepositLifecycle
{
    /// <summary>Seed state before any event has folded.</summary>
    Pending,

    /// <summary>Constituted and accruing — between DepositConstituted and DepositMatured.</summary>
    Active,

    /// <summary>Matured and paid out — terminal for the AT_MATURITY slice.</summary>
    Matured,

    /// <summary>Constitution was rejected by a config/rule check — no deposit was opened (terminal).</summary>
    Failed,

    /// <summary>Rolled over into a new term/deposit at renewal — terminal for this deposit id.</summary>
    Renewed,

    /// <summary>Broken before maturity and settled net of penalty (terminal).</summary>
    TerminatedEarly,

    /// <summary>Balance transferred to the holder's heirs on succession (terminal).</summary>
    TransferredToHeirs,

    /// <summary>GDPR Article 17 right-to-be-forgotten exercised — the subject's PII key was
    /// crypto-shredded (ADR-PC-004 §P3) and only non-personal structural fields remain queryable
    /// (terminal). Reachable from any non-Pending state: erasure is a regulatory obligation that can
    /// land on a live OR an already-closed deposit (a matured/terminated deposit still holds the
    /// subject's PII until erased).</summary>
    Erased,
}

/// <summary>
/// The deposit-position projection: the term-deposit aggregate's folded state (E.1,
/// archie-uqlm). This record IS the minimal "sync" projection — there is no separate
/// read-model table. It is produced by folding the family's four events through the
/// existing engine mechanism (<see cref="Babelstone.Engine.SimulationRuntime{TState}"/>
/// for the in-memory read, <see cref="Babelstone.Engine.AggregateRuntime{TState}"/> for the
/// durable read-through), so a read of the just-committed log always reflects the latest
/// event (the two-modes §5.4 "sync" definition).
/// </summary>
/// <remarks>
/// Two layers materialise this state into the bitemporal store, and they are distinct
/// (babelstone-p0u4): <b>D.1</b> shipped the GENERIC, family-agnostic engine projection storage —
/// the <c>projections</c> table and <c>ProjectionRecord</c>/<c>IProjectionStorage</c>/
/// <c>PostgresProjectionStore</c> in <c>Babelstone.EventStore</c>, which "does not know what a
/// deposit is" (feature-design-event-store-projections §3). <b>F.6</b> is where THIS family
/// materialises its family-specific <c>deposit_position</c> projection — a projector folding the
/// family's events and writing through that store, declared in
/// <see cref="TermDepositProjectionModule"/> (kind <c>term_deposit.deposit_position</c>), alongside
/// the accrual schedule, maturity calendar, and withholding ledger. The family-layer
/// <c>deposit_position</c> name does not collide with the engine's generic <c>projections</c> table.
/// All monetary fields are <see cref="Money"/> (cents); no <c>decimal</c> state lives here
/// (ADR-PC-010 §P1, BMNY002).
/// </remarks>
/// <param name="PaymentPeriodMonths">The PERIODIC coupon cadence in months (1 or 3), folded from
/// <c>DepositConstituted</c>; 0 for AT_MATURITY/ADVANCE. Lets the service derive coupon windows.</param>
/// <param name="CouponsPaid">How many PERIODIC coupons have already been paid out (folded from
/// each <c>InterestPaid</c>). The service derives "which coupon is next" from this count plus the
/// start date and cadence — no clock or wall-time in the fold (BENG001/002/003).</param>
/// <param name="ProductCode">The catalogue product code (e.g. <c>dpz_pt_12m_juros_venc</c>), folded
/// from <c>DepositConstituted.ProductCode</c> — the structural product identifier the D.4 read model
/// denormalizes (bd babelstone-v794). Empty ("") for deposits constituted before v794, which never
/// carried it (the Avro default decodes to ""); not PII (ADR-PC-004 §P2).</param>
/// <param name="Role">The pricing role the rate sheet priced the TAN against (e.g. <c>standard</c>),
/// folded from <c>DepositConstituted.Role</c> — a STRUCTURAL pricing dimension, NOT PII
/// (ADR-PC-004 §P2). Persisted so the engine re-resolves the SAME <c>(product, role)</c> rate at
/// auto-renewal from the closing deposit, keeping product knowledge out of the orchestrator
/// (bd babelstone-mtto.5). Empty ("") for deposits constituted before mtto.5 (the Avro default).</param>
/// <param name="FundingAccount">The OPAQUE funding-account token the principal was debited (a
/// reference the engine resolves internally, NOT an IBAN/cleartext — ADR-PC-004 §P2), folded from
/// <c>DepositConstituted.FundingAccount</c>. Persisted so the engine settles the auto-renewal
/// rollover debit against the SAME funding reference from the closing deposit (bd babelstone-mtto.5).
/// Empty ("") for deposits constituted before mtto.5 (the Avro default).</param>
/// <param name="MinWithdrawalCents">The F.12 partial-withdrawal policy PINNED at constitution (bd
/// k6r8.8/qze9), folded from <c>DepositConstituted</c>: the smallest partial withdrawal the product
/// allows, in cents (PartialWithdrawalPolicy.MinWithdrawalCents). The partial-withdrawal command path
/// rebuilds the policy from these three folded fields, so the rules a live deposit is subject to are
/// the ones fixed at constitution — not whatever the product config says later. 0 ⇒ Unrestricted (the
/// value pre-F.12 deposits decode from the Avro default). Structural config, NOT PII (ADR-PC-004 §P2).</param>
/// <param name="MinRemainingBalanceCents">The F.12 minimum remaining balance after a withdrawal, in
/// cents, PINNED at constitution (PartialWithdrawalPolicy.MinRemainingBalanceCents). 0 ⇒ no floor.</param>
/// <param name="CarenciaDays">The F.12 lock-up (carência) window in days from constitution, PINNED at
/// constitution (PartialWithdrawalPolicy.CarenciaDays). A duration, not money. 0 ⇒ no lock-up.</param>
/// <param name="PrincipalTimeline">The deposit's principal as a STEP FUNCTION of time (F.12, bd
/// babelstone-emtr): the ordered <see cref="PrincipalSegment"/>s the accrual engine prices interest
/// over. Seeded with the opening <c>(StartDate, Principal)</c> at constitution and appended
/// <c>(WithdrawnOn, RemainingPrincipal)</c> by each partial withdrawal — so interest accrued and the
/// maturity principal-return reflect the principal ACTUALLY held over each sub-period, not the original
/// constituted amount. A deposit that never partially withdraws has a single-segment timeline, which
/// accrues byte-for-byte as before. A deterministic fold of the events — no clock, no I/O
/// (BENG001/002/003); rebuilt identically on cold replay (ADR-PC-010 §P5).</param>
public sealed record DepositPosition(
    Guid DepositId,
    Money Principal,
    int TanBasisPoints,
    string RateSheetVersionId,
    int TermDays,
    DateOnly StartDate,
    DateOnly MaturityDate,
    string InterestVariant,
    string AutoRenewalPolicy,
    int PaymentPeriodMonths,
    string ProductCode,
    string Role,
    string FundingAccount,
    long MinWithdrawalCents,
    long MinRemainingBalanceCents,
    int CarenciaDays,
    IReadOnlyList<PrincipalSegment> PrincipalTimeline,
    Money AccruedGrossInterest,
    Money WithholdingToDate,
    Money NetInterest,
    Money TotalPayout,
    Money RemainingPrincipal,
    Money SettlementAmount,
    int CorrectionCount,
    int CouponsPaid,
    DepositLifecycle Lifecycle)
{
    /// <summary>The seed state a fold starts from (before <c>DepositConstituted</c>).</summary>
    public static DepositPosition Empty { get; } = new(
        DepositId: Guid.Empty,
        Principal: Money.Zero,
        TanBasisPoints: 0,
        RateSheetVersionId: string.Empty,
        TermDays: 0,
        StartDate: default,
        MaturityDate: default,
        InterestVariant: string.Empty,
        AutoRenewalPolicy: string.Empty,
        PaymentPeriodMonths: 0,
        ProductCode: string.Empty,
        Role: string.Empty,
        FundingAccount: string.Empty,
        MinWithdrawalCents: 0,
        MinRemainingBalanceCents: 0,
        CarenciaDays: 0,
        // Empty until DepositConstituted seeds the opening (start, principal) segment.
        PrincipalTimeline: [],
        AccruedGrossInterest: Money.Zero,
        WithholdingToDate: Money.Zero,
        NetInterest: Money.Zero,
        TotalPayout: Money.Zero,
        RemainingPrincipal: Money.Zero,
        SettlementAmount: Money.Zero,
        CorrectionCount: 0,
        CouponsPaid: 0,
        Lifecycle: DepositLifecycle.Pending);
}
