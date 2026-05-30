using Babelstone.FinancialTypes;

namespace Babelstone.Families.TermDeposit;

/// <summary>The lifecycle states the minimal AT_MATURITY slice transits (E.1). The full
/// state machine (early termination, renewal) is Epic F (F.3); this slice is
/// constitute → mature only.</summary>
public enum DepositLifecycle
{
    /// <summary>Seed state before any event has folded.</summary>
    Pending,

    /// <summary>Constituted and accruing — between DepositConstituted and DepositMatured.</summary>
    Active,

    /// <summary>Matured and paid out — terminal for the AT_MATURITY slice.</summary>
    Matured,
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
    Money AccruedGrossInterest,
    Money WithholdingToDate,
    Money NetInterest,
    Money TotalPayout,
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
        AccruedGrossInterest: Money.Zero,
        WithholdingToDate: Money.Zero,
        NetInterest: Money.Zero,
        TotalPayout: Money.Zero,
        Lifecycle: DepositLifecycle.Pending);
}
