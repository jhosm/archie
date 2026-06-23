using Babelstone.Engine;
using Babelstone.FinancialTypes;
using Xunit;

namespace Babelstone.Families.TermDeposit.Tests;

/// <summary>
/// B.10 mutation backstop for the events' <see cref="DomainEvent.IsLifecycleBoundary"/> flag. The
/// engine OR's this flag across the events it just folded to decide whether to cut a snapshot at a
/// lifecycle boundary (<c>SnapshotContext.IsLifecycleBoundary</c>, ADR-PC-010 §P5). A boundary event
/// silently flipped to <c>false</c> would stop forcing that snapshot — a latent correctness/recovery
/// regression no fold assertion catches. These tests pin which term-deposit events ARE boundaries
/// (constitution, maturity, renewal, early termination, partial withdrawal, succession transfer) and
/// which are ordinary ledger events (accrual, withholding, coupon payout, constitution failure,
/// correction), killing the <c>=&gt; true</c> boolean mutants and the base <c>=&gt; false</c> default.
/// </summary>
public class EventMetadataTests
{
    private static readonly Guid Id = Guid.NewGuid();
    private static readonly DateOnly OnDate = new(2026, 6, 1);

    private static readonly DomainEvent[] LifecycleBoundaryEvents =
    [
        new DepositConstituted(
            DepositId: Id, Principal: new Money(1_000_000L), TanBasisPoints: 300,
            RateSheetVersionId: "rs-1", TermDays: 365, StartDate: new DateOnly(2026, 1, 1),
            MaturityDate: new DateOnly(2027, 1, 1), InterestVariant: "AT_MATURITY", AutoRenewalPolicy: "NONE"),
        new DepositMatured(
            PrincipalReturned: new Money(1_000_000L), NetInterestPaid: new Money(21_900L),
            TotalPayout: new Money(1_021_900L), MaturedOn: new DateOnly(2027, 1, 1)),
        new DepositRenewed(
            DepositId: Id, NewDepositId: Guid.NewGuid(), RolloverPrincipal: new Money(1_000_000L),
            NewRateSheetVersionId: "rs-2", NewTanBasisPoints: 300, NewTermDays: 365,
            RenewalDate: new DateOnly(2027, 1, 1), NewMaturityDate: new DateOnly(2028, 1, 1)),
        new DepositTerminatedEarly(
            DepositId: Id, PrincipalReturned: new Money(1_000_000L), PenaltyAmount: new Money(1_500L),
            NetSettlementAmount: new Money(1_018_400L), TerminatedOn: OnDate, TerminationReason: "CUSTOMER_REQUEST"),
        new DepositPartiallyWithdrawn(
            DepositId: Id, WithdrawnAmount: new Money(200_000L),
            RemainingPrincipal: new Money(800_000L), WithdrawnOn: OnDate),
        new DepositTransferredToHeirs(
            DepositId: Id, HeirCaseRef: "succ-case-1",
            TransferredBalance: new Money(1_021_900L), TransferDate: OnDate),
    ];

    private static readonly DomainEvent[] OrdinaryEvents =
    [
        new InterestAccrued(new Money(10_000L), OnDate),
        new WithholdingApplied(new Money(2_800L), new Money(7_200L)),
        new InterestPaid(Id, new Money(7_500L), new Money(2_100L), new Money(5_400L), OnDate),
        new DepositConstitutionFailed(Id, "RATE_SHEET_NOT_FOUND", "no sheet pinned for term_deposit"),
        new DepositCorrected(Id, "corr-1", "principal", "prev-ref", "new-ref", OnDate, "TYPO"),
    ];

    [Fact]
    public void Lifecycle_boundary_events_force_a_snapshot()
    {
        Assert.All(LifecycleBoundaryEvents, e =>
            Assert.True(e.IsLifecycleBoundary, $"{e.GetType().Name} must be a lifecycle boundary"));
    }

    [Fact]
    public void Ordinary_ledger_events_are_not_lifecycle_boundaries()
    {
        Assert.All(OrdinaryEvents, e =>
            Assert.False(e.IsLifecycleBoundary, $"{e.GetType().Name} must NOT be a lifecycle boundary"));
    }
}
