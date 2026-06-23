using Babelstone.Engine;
using Babelstone.FinancialTypes;
using Xunit;

namespace Babelstone.Families.PersonalLoan.Tests;

/// <summary>
/// Replay/fold tests for the personal_loan loan position: the pure folds LABEL state and accumulate the
/// already-computed facts the events carry, never recomputing the amortization split. These pin the
/// closed-end-asset shape (disburse → amortize → settle / write off) and the byte-identical replay
/// determinism the engine relies on (ADR-PC-010 §P5). They use the family's OWN handler registry — the
/// same one the durable runtime and the projection runner fold through.
/// </summary>
public sealed class LoanPositionFoldTests
{
    private static readonly HandlerRegistry Registry = PersonalLoanFamilyModule.Registry();

    [Fact]
    public void Disbursement_opens_an_Active_loan_owing_the_full_principal()
    {
        var loanId = Guid.NewGuid();
        var position = Fold(LoanPosition.Empty, Disbursed(loanId, principalCents: 1_000_000, termMonths: 12));

        Assert.Equal(loanId, position.LoanId);
        Assert.Equal(LoanLifecycle.Active, position.Lifecycle);
        Assert.Equal(new Money(1_000_000), position.Principal);
        // The loan opens owing the full disbursed capital; the balance amortizes from here.
        Assert.Equal(new Money(1_000_000), position.OutstandingBalance);
        Assert.Equal(0, position.InstallmentsPaid);
        Assert.Equal(12, position.TermMonths);
    }

    [Fact]
    public void Paying_installments_amortizes_the_balance_and_accumulates_the_legs()
    {
        var loanId = Guid.NewGuid();
        var position = Fold(LoanPosition.Empty, Disbursed(loanId, principalCents: 1_000_000, termMonths: 12));

        // Two installments: each carries the post-installment balance the decider computed.
        position = Fold(position, new LoanInstallmentPaid(
            loanId, 1, new Money(5_000), new Money(81_066), new Money(918_934), new DateOnly(2026, 2, 1)));
        position = Fold(position, new LoanInstallmentPaid(
            loanId, 2, new Money(4_595), new Money(81_471), new Money(837_463), new DateOnly(2026, 3, 1)));

        Assert.Equal(2, position.InstallmentsPaid);
        Assert.Equal(new Money(837_463), position.OutstandingBalance);
        Assert.Equal(new Money(5_000 + 4_595), position.TotalInterestPaid);
        Assert.Equal(new Money(81_066 + 81_471), position.TotalCapitalRepaid);
        Assert.Equal(LoanLifecycle.Active, position.Lifecycle); // still amortizing
    }

    [Fact]
    public void Full_early_repayment_then_settlement_closes_the_loan()
    {
        var loanId = Guid.NewGuid();
        var position = Fold(LoanPosition.Empty, Disbursed(loanId, principalCents: 1_000_000, termMonths: 12));

        // A full early repayment drives the balance to zero (capital + capped commission), and the
        // paired LoanSettled folds the loan to Settled.
        position = Fold(position, new LoanRepaidEarly(
            loanId, new Money(1_000_000), new Money(5_000), Money.Zero, new DateOnly(2026, 6, 1)));
        Assert.Equal(Money.Zero, position.OutstandingBalance);
        Assert.Equal(new Money(1_000_000), position.TotalCapitalRepaid);
        Assert.Equal(new Money(5_000), position.TotalCommissionCharged);
        Assert.Equal(LoanLifecycle.Active, position.Lifecycle); // repayment alone does not close

        position = Fold(position, new LoanSettled(new Money(1_000_000), Money.Zero, new DateOnly(2026, 6, 1)));
        Assert.Equal(LoanLifecycle.Settled, position.Lifecycle);
    }

    [Fact]
    public void Write_off_records_the_loss_and_zeroes_the_balance()
    {
        var loanId = Guid.NewGuid();
        var position = Fold(LoanPosition.Empty, Disbursed(loanId, principalCents: 1_000_000, termMonths: 12));
        position = Fold(position, new LoanInstallmentPaid(
            loanId, 1, new Money(5_000), new Money(81_066), new Money(918_934), new DateOnly(2026, 2, 1)));

        position = Fold(position, new LoanWrittenOff(
            loanId, new Money(918_934), new DateOnly(2026, 9, 1), "DEFAULT_UNRECOVERABLE"));

        Assert.Equal(LoanLifecycle.WrittenOff, position.Lifecycle);
        Assert.Equal(Money.Zero, position.OutstandingBalance);
        // The unrecovered capital is recorded on the position as a loss — NOT folded into the repaid
        // tally (it was not repaid) and NOT dropped (bd babelstone-5r9n.8).
        Assert.Equal(new Money(918_934), position.WrittenOffAmount);
        Assert.Equal(new Money(81_066), position.TotalCapitalRepaid);
        // Principal reconciles from the position ALONE: capital repaid + loss == the original principal,
        // no event-log replay needed.
        Assert.Equal(position.Principal, position.TotalCapitalRepaid + position.WrittenOffAmount);
    }

    [Fact]
    public void A_written_off_loan_is_distinguishable_from_a_settled_one_without_reading_the_log()
    {
        // Both terminals end with a zero OutstandingBalance, so the balance alone cannot tell them apart.
        // The WrittenOffAmount field is the discriminator: zero on a settled loan, the loss on a written
        // off one (bd babelstone-5r9n.8).
        var settledId = Guid.NewGuid();
        var settled = Fold(LoanPosition.Empty, Disbursed(settledId, principalCents: 1_000_000, termMonths: 12));
        settled = Fold(settled, new LoanRepaidEarly(
            settledId, new Money(1_000_000), new Money(5_000), Money.Zero, new DateOnly(2026, 6, 1)));
        settled = Fold(settled, new LoanSettled(new Money(1_000_000), Money.Zero, new DateOnly(2026, 6, 1)));

        var writtenOffId = Guid.NewGuid();
        var writtenOff = Fold(LoanPosition.Empty, Disbursed(writtenOffId, principalCents: 1_000_000, termMonths: 12));
        writtenOff = Fold(writtenOff, new LoanWrittenOff(
            writtenOffId, new Money(1_000_000), new DateOnly(2026, 9, 1), "DEFAULT_UNRECOVERABLE"));

        // Same zero outstanding balance on both terminals…
        Assert.Equal(Money.Zero, settled.OutstandingBalance);
        Assert.Equal(Money.Zero, writtenOff.OutstandingBalance);
        // …but the loss field separates them with no log read.
        Assert.Equal(Money.Zero, settled.WrittenOffAmount);
        Assert.Equal(new Money(1_000_000), writtenOff.WrittenOffAmount);
        Assert.NotEqual(settled.Lifecycle, writtenOff.Lifecycle);
    }

    [Fact]
    public void Disbursement_failure_folds_to_Failed_with_no_loan_opened()
    {
        var loanId = Guid.NewGuid();
        var position = Fold(LoanPosition.Empty, new LoanDisbursementFailed(
            loanId, "ELIGIBILITY_NOT_MET", "Required precondition(s) absent: solvency_assessed."));

        Assert.Equal(loanId, position.LoanId);
        Assert.Equal(LoanLifecycle.Failed, position.Lifecycle);
        Assert.Equal(Money.Zero, position.Principal); // no loan was opened
    }

    [Fact]
    public void Erasure_folds_to_Erased_leaving_structural_fields_queryable()
    {
        var loanId = Guid.NewGuid();
        var position = Fold(LoanPosition.Empty, Disbursed(loanId, principalCents: 1_000_000, termMonths: 12));

        position = Fold(position, new PersonalDataErasureRequested(
            loanId, "pseudo-abc", new DateOnly(2027, 1, 1), "GDPR_ARTICLE_17"));

        Assert.Equal(LoanLifecycle.Erased, position.Lifecycle);
        // Structural fields stay queryable post-erasure (the personal data lived behind the OpenBao key).
        Assert.Equal(new Money(1_000_000), position.Principal);
        Assert.Equal(loanId, position.LoanId);
    }

    [Fact]
    public void Cold_replay_reproduces_a_byte_identical_position()
    {
        // Folds are deterministic — re-folding the same event sequence yields an equal position
        // (record value equality; no collection fields, so the synthesized equality is correct).
        var loanId = Guid.NewGuid();
        var events = new DomainEvent[]
        {
            Disbursed(loanId, principalCents: 1_000_000, termMonths: 12),
            new LoanInstallmentPaid(loanId, 1, new Money(5_000), new Money(81_066), new Money(918_934), new DateOnly(2026, 2, 1)),
            new LoanInstallmentPaid(loanId, 2, new Money(4_595), new Money(81_471), new Money(837_463), new DateOnly(2026, 3, 1)),
            new LoanRepaidEarly(loanId, new Money(837_463), new Money(4_187), Money.Zero, new DateOnly(2026, 4, 1)),
            new LoanSettled(new Money(1_000_000), new Money(9_595), new DateOnly(2026, 4, 1)),
        };

        var first = events.Aggregate(LoanPosition.Empty, Fold);
        var second = events.Aggregate(LoanPosition.Empty, Fold);

        Assert.Equal(first, second);
        Assert.Equal(LoanLifecycle.Settled, first.Lifecycle);
    }

    // --- helpers ---

    private static LoanDisbursed Disbursed(Guid loanId, long principalCents, int termMonths) => new(
        LoanId: loanId,
        Principal: new Money(principalCents),
        TanBasisPoints: 600,
        RateSheetVersionId: "rs-1",
        TermMonths: termMonths,
        PeriodicRateBasisPoints: 50,
        InstallmentAmount: new Money(86_066),
        StartDate: new DateOnly(2026, 1, 1),
        FirstInstallmentDate: new DateOnly(2026, 2, 1),
        Purpose: "general",
        ProductCode: "cp_pt_general_12m",
        DisbursementAccountRef: "acct-token-1",
        EarlyRepaymentCommissionBps: 50);

    private static LoanPosition Fold(LoanPosition state, DomainEvent @event)
    {
        // event_type mirrors the engine's binding: a family event is `personal_loan.<Name>`, while an
        // engine-declared cross-cutting event (Babelstone.Engine namespace, e.g. PackVersionMigrated /
        // PersonalDataErasureRequested) is `operations.<Name>` (event-store §4.3).
        var name = @event.GetType().Name;
        var eventType = @event.GetType().Namespace == "Babelstone.Engine"
            ? $"operations.{name}"
            : $"personal_loan.{name}";
        Assert.True(Registry.TryResolve(eventType, out var handler), $"no handler for {eventType}");
        return (LoanPosition)handler.ApplyBoxed(state, @event).NewState;
    }
}
