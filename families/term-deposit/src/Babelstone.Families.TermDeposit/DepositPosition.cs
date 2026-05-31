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
/// The full materialised bitemporal <c>deposit_position</c> table
/// (valid_from/valid_to/recorded_at/superseded_at, a projector that writes through the
/// sink) is Epic D (D.1) / Epic F (F.6) — deliberately excluded from the walking skeleton.
/// All monetary fields are <see cref="Money"/> (cents); no <c>decimal</c> state lives here
/// (ADR-PC-010 §P1, BMNY002).
/// </remarks>
/// <param name="PaymentPeriodMonths">The PERIODIC coupon cadence in months (1 or 3), folded from
/// <c>DepositConstituted</c>; 0 for AT_MATURITY/ADVANCE. Lets the service derive coupon windows.</param>
/// <param name="CouponsPaid">How many PERIODIC coupons have already been paid out (folded from
/// each <c>InterestPaid</c>). The service derives "which coupon is next" from this count plus the
/// start date and cadence — no clock or wall-time in the fold (BENG001/002/003).</param>
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
