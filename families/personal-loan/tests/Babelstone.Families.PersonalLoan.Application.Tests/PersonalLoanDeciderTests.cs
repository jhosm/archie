using Babelstone.Engine;
using Babelstone.FinancialTypes;
using Babelstone.Families.PersonalLoan.Application;
using Xunit;

namespace Babelstone.Families.PersonalLoan.Application.Tests;

/// <summary>
/// The pure personal_loan decider (ADR-PC-021 §P3): command + resolved inputs → events, running the
/// amortization kernel command-side. Docker-free unit tests — every input is explicit (ADR-PC-010 §P5).
/// They cover the disbursement stamp, the per-installment split derived from the schedule, the capped
/// early repayment (full + partial), the precondition refusal (ADR-PC-024), and the lifecycle pairing
/// (a final installment / full repayment pairs with a closing LoanSettled).
/// </summary>
public sealed class PersonalLoanDeciderTests
{
    [Fact]
    public void DecideDisbursement_stamps_the_resolved_rate_periodic_rate_and_level_installment()
    {
        var loanId = Guid.NewGuid();
        var command = DisburseCommand(loanId, principalCents: 1_000_000, termMonths: 12);

        var disbursed = PersonalLoanDecider.DecideDisbursement(command, tanBasisPoints: 600, rateSheetVersionId: "rs-1");

        Assert.Equal(loanId, disbursed.LoanId);
        Assert.Equal(new Money(1_000_000), disbursed.Principal);
        Assert.Equal(600, disbursed.TanBasisPoints);
        Assert.Equal(50, disbursed.PeriodicRateBasisPoints); // 600 / 12
        Assert.Equal(new Money(86_066), disbursed.InstallmentAmount); // €860.66 (fin-math §4.1)
        Assert.Equal(new DateOnly(2026, 2, 1), disbursed.FirstInstallmentDate); // start + 1 cadence
        Assert.Equal("rs-1", disbursed.RateSheetVersionId);
        Assert.Equal("general", disbursed.Purpose);
    }

    [Fact]
    public void DecideDisbursement_records_an_originated_credit_movement_append_first_against_the_borrower_account()
    {
        // ADR-PC-032 slot 5 / §A8 / feature-design §134: the disbursement records its money leg APPEND-FIRST
        // as ONE Originated Credit Movement against the borrower's disbursement account — the lump sum ENTERS
        // that account (Credit), correcting the old eager Debit-on-the-borrower wrinkle. The substrate-owned
        // settlement saga effects the cash leg, gated, off this recorded Movement; the decider settles nothing.
        var loanId = Guid.NewGuid();
        var commandId = Guid.NewGuid();
        var command = DisburseCommand(loanId, principalCents: 1_000_000, termMonths: 12) with { CommandId = commandId };

        var disbursed = PersonalLoanDecider.DecideDisbursement(command, tanBasisPoints: 600, rateSheetVersionId: "rs-1");

        var movement = Assert.Single(disbursed.Movements!);
        Assert.Equal(SettlementDirection.Credit, movement.Direction);        // value ENTERS the borrower's account
        Assert.Equal("acct-token-1", movement.AccountRef);                   // pinned to the disbursement account
        Assert.Equal(new Money(1_000_000), movement.Amount);                 // the disbursed principal
        Assert.Equal(MovementOperation.Disburse, movement.Operation);        // the closed operation code
        Assert.Equal(MovementOrigin.Originated, movement.Origin);            // the decider decided it → gated cash leg
        Assert.Equal(new DateOnly(2026, 1, 1), movement.ValueDate);          // the disbursement date, not a clock
        Assert.Equal(commandId, movement.CommandId);                        // threaded for append idempotency (slot 4)
    }

    [Fact]
    public void DecideDisbursement_movement_promotes_originated_credit_headers_for_the_settlement_saga()
    {
        // The producer hop (bd babelstone-t7o3.20): the recorded Movement promotes ce_movementorigin=Originated
        // and ce_movementdirection=Credit, the headers the substrate-owned settlement saga auto-starts on.
        var command = DisburseCommand(Guid.NewGuid(), principalCents: 500_000, termMonths: 24);
        var disbursed = PersonalLoanDecider.DecideDisbursement(command, tanBasisPoints: 600, rateSheetVersionId: "rs-1");

        var headers = disbursed.IntegrationHeaders;
        Assert.NotNull(headers);
        Assert.Equal("Originated", headers![MovementHeaders.OriginKey]);
        Assert.Equal("Credit", headers[MovementHeaders.DirectionKey]);
    }

    [Fact]
    public void DecideInstallment_emits_the_next_schedule_rows_split()
    {
        // The first installment of the worked example: interest €50.00, capital €810.66, balance €9,189.34.
        var position = DisbursedPosition(principalCents: 1_000_000, periodicRateBps: 50, termMonths: 12, installmentsPaid: 0);

        var events = PersonalLoanDecider.DecideInstallment(position, new DateOnly(2026, 2, 1));
        var paid = Assert.IsType<LoanInstallmentPaid>(Assert.Single(events));

        Assert.Equal(1, paid.InstallmentNumber);
        Assert.Equal(new Money(5_000), paid.Interest);
        Assert.Equal(new Money(81_066), paid.Capital);
        Assert.Equal(new Money(918_934), paid.OutstandingBalance);
    }

    [Fact]
    public void DecideInstallment_advances_to_the_correct_row_by_installments_paid()
    {
        // After 2 paid, the next installment is row 3 (interest €41.87 on opening €8,374.63).
        var position = DisbursedPosition(principalCents: 1_000_000, periodicRateBps: 50, termMonths: 12, installmentsPaid: 2);
        var events = PersonalLoanDecider.DecideInstallment(position, new DateOnly(2026, 4, 1));
        var paid = (LoanInstallmentPaid)events[^1];

        Assert.Equal(3, paid.InstallmentNumber);
        Assert.Equal(new Money(4_187), paid.Interest);
    }

    [Fact]
    public void DecideInstallment_rejects_when_all_installments_are_paid()
    {
        var position = DisbursedPosition(principalCents: 1_000_000, periodicRateBps: 50, termMonths: 12, installmentsPaid: 12);
        Assert.Throws<DomainRejectedException>(
            () => PersonalLoanDecider.DecideInstallment(position, new DateOnly(2027, 1, 1)));
    }

    [Fact]
    public void DecideFinalInstallment_pairs_the_last_installment_with_a_settlement()
    {
        // The 12th installment closes the balance to zero, so it pairs with a LoanSettled.
        var position = DisbursedPosition(principalCents: 1_000_000, periodicRateBps: 50, termMonths: 12, installmentsPaid: 11);
        var events = PersonalLoanDecider.DecideFinalInstallment(position, new DateOnly(2027, 1, 1));

        Assert.Equal(2, events.Count);
        var paid = Assert.IsType<LoanInstallmentPaid>(events[0]);
        Assert.Equal(12, paid.InstallmentNumber);
        Assert.Equal(Money.Zero, paid.OutstandingBalance); // fully amortized
        Assert.IsType<LoanSettled>(events[1]);
    }

    [Fact]
    public void DecideEarlyRepayment_full_repayment_caps_commission_and_settles()
    {
        // Full repayment of the €10,000 balance with >1y remaining (24 installments left) ⇒ commission
        // capped at 0.50% = €50.00; the balance reaches zero so a LoanSettled is paired.
        var position = DisbursedPosition(principalCents: 1_000_000, periodicRateBps: 50, termMonths: 36, installmentsPaid: 0)
            with { OutstandingBalance = new Money(1_000_000), TermMonths = 36 };

        var events = PersonalLoanDecider.DecideEarlyRepayment(
            position, new Money(1_000_000), new DateOnly(2026, 6, 1), remainingInstallments: 36);

        var repaid = Assert.IsType<LoanRepaidEarly>(events[0]);
        Assert.Equal(new Money(1_000_000), repaid.CapitalRepaid);
        Assert.Equal(new Money(5_000), repaid.Commission); // 0.50% of €10,000 = €50.00
        Assert.Equal(Money.Zero, repaid.OutstandingBalanceAfter);
        Assert.IsType<LoanSettled>(events[1]); // full repayment settles
    }

    [Fact]
    public void DecideEarlyRepayment_uses_the_tighter_cap_when_one_year_or_less_remains()
    {
        // With ≤1 year remaining (12 installments left), the statutory cap is 0.25%: €5,000 repaid ⇒ the
        // commission caps at €12.50, even though the product charges 0.50%.
        var position = DisbursedPosition(principalCents: 1_000_000, periodicRateBps: 50, termMonths: 36, installmentsPaid: 24)
            with { OutstandingBalance = new Money(500_000), TermMonths = 36, EarlyRepaymentCommissionBps = 50 };

        var events = PersonalLoanDecider.DecideEarlyRepayment(
            position, new Money(500_000), new DateOnly(2027, 1, 1), remainingInstallments: 12);

        var repaid = Assert.IsType<LoanRepaidEarly>(events[0]);
        Assert.Equal(new Money(1_250), repaid.Commission); // 0.25% of €5,000 = €12.50
        Assert.IsType<LoanSettled>(events[1]); // full repayment of the remaining balance settles
    }

    [Fact]
    public void DecideEarlyRepayment_partial_reduces_the_balance_and_stays_open()
    {
        var position = DisbursedPosition(principalCents: 1_000_000, periodicRateBps: 50, termMonths: 36, installmentsPaid: 0)
            with { OutstandingBalance = new Money(1_000_000), TermMonths = 36 };

        var events = PersonalLoanDecider.DecideEarlyRepayment(
            position, new Money(400_000), new DateOnly(2026, 6, 1), remainingInstallments: 36);

        var repaid = Assert.IsType<LoanRepaidEarly>(Assert.Single(events)); // no settlement — still open
        Assert.Equal(new Money(600_000), repaid.OutstandingBalanceAfter);
    }

    [Fact]
    public void DecideEarlyRepayment_rejects_a_repayment_exceeding_the_balance()
    {
        var position = DisbursedPosition(principalCents: 1_000_000, periodicRateBps: 50, termMonths: 36, installmentsPaid: 0)
            with { OutstandingBalance = new Money(500_000), TermMonths = 36 };

        Assert.Throws<DomainRejectedException>(() => PersonalLoanDecider.DecideEarlyRepayment(
            position, new Money(600_000), new DateOnly(2026, 6, 1), remainingInstallments: 36));
    }

    [Fact]
    public void CheckPreconditions_refuses_when_a_required_verdict_is_absent()
    {
        var loanId = Guid.NewGuid();
        var required = new[] { PersonalLoanDecider.PreconditionSolvencyAssessed };

        // No verdicts supplied ⇒ refusal.
        var refusal = PersonalLoanDecider.CheckPreconditions(loanId, required, verdicts: null);

        Assert.NotNull(refusal);
        Assert.Equal(PersonalLoanDecider.EligibilityNotMetReason, refusal!.FailureReason);
        Assert.Contains("solvency_assessed", refusal.FailureDetail);
    }

    [Fact]
    public void CheckPreconditions_passes_when_every_required_verdict_is_satisfied()
    {
        var loanId = Guid.NewGuid();
        var required = new[] { PersonalLoanDecider.PreconditionSolvencyAssessed };
        var verdicts = new Dictionary<string, PreconditionVerdict>
        {
            [PersonalLoanDecider.PreconditionSolvencyAssessed] =
                new(Satisfied: true, EvidenceRef: "ref-1", EvaluatedAt: DateTimeOffset.UnixEpoch),
        };

        Assert.Null(PersonalLoanDecider.CheckPreconditions(loanId, required, verdicts));
    }

    [Fact]
    public void CheckPreconditions_records_the_verdict_lineage_on_the_refusal()
    {
        var loanId = Guid.NewGuid();
        var required = new[] { "crc_consulted", "solvency_assessed" };
        var verdicts = new Dictionary<string, PreconditionVerdict>
        {
            ["solvency_assessed"] = new(Satisfied: false, EvidenceRef: "ref-2", EvaluatedAt: DateTimeOffset.UnixEpoch),
        };

        var refusal = PersonalLoanDecider.CheckPreconditions(loanId, required, verdicts);

        Assert.NotNull(refusal);
        // The recorded lineage is ordered by key (replay-identical) and carries the resolved verdicts.
        Assert.NotNull(refusal!.Preconditions);
        Assert.Single(refusal.Preconditions!);
        Assert.Equal("solvency_assessed", refusal.Preconditions![0].Key);
        Assert.False(refusal.Preconditions[0].Satisfied);
    }

    [Fact]
    public void Ungated_product_is_never_refused()
        => Assert.Null(PersonalLoanDecider.CheckPreconditions(Guid.NewGuid(), Array.Empty<string>(), verdicts: null));

    // --- helpers ---

    private static DisburseLoanCommand DisburseCommand(Guid loanId, long principalCents, int termMonths) => new(
        LoanId: loanId,
        PrincipalCents: principalCents,
        ProductId: "cp_pt_general_12m",
        Role: "standard",
        TermMonths: termMonths,
        StartDate: new DateOnly(2026, 1, 1),
        DisbursedAt: new DateTimeOffset(2026, 1, 1, 9, 0, 0, TimeSpan.Zero),
        Purpose: "general",
        DisbursementAccountRef: "acct-token-1",
        Actor: "test",
        EarlyRepaymentCommissionBps: 50);

    private static LoanPosition DisbursedPosition(
        long principalCents, int periodicRateBps, int termMonths, int installmentsPaid) =>
        LoanPosition.Empty with
        {
            LoanId = Guid.NewGuid(),
            Principal = new Money(principalCents),
            PeriodicRateBasisPoints = periodicRateBps,
            TermMonths = termMonths,
            InstallmentsPaid = installmentsPaid,
            OutstandingBalance = new Money(principalCents),
            EarlyRepaymentCommissionBps = 50,
            Lifecycle = LoanLifecycle.Active,
        };
}
