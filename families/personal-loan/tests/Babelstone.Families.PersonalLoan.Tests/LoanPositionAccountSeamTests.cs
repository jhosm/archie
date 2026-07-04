using Babelstone.Engine;
using Babelstone.FinancialTypes;
using Xunit;

namespace Babelstone.Families.PersonalLoan.Tests;

/// <summary>
/// Account-seam conformance (ADR-PC-033 slot 1, bd babelstone-hkwp.6): the personal loan is a
/// DEGENERATE account — one balance, no holds — so <see cref="LoanPosition"/> implements the
/// spine-owned <see cref="IAccount"/> seam (the same one-member family-declared idiom as
/// <see cref="IErasable{TState}"/>). This is a RECLASSIFICATION, not a behaviour change: these
/// tests pin that the <c>account_ref</c> is the loan's own opaque stream id (never PII —
/// ADR-PC-004 §P2), that it is stable across the fold, that the loan is NOT
/// <see cref="IHoldable"/> (it carries no holds), and that the compiler-synthesised record
/// equality — the replay-determinism backstop — is untouched by the computed property.
/// </summary>
public sealed class LoanPositionAccountSeamTests
{
    private static readonly HandlerRegistry Registry = PersonalLoanFamilyModule.Registry();

    [Fact]
    public void LoanPosition_implements_the_IAccount_seam_but_not_IHoldable()
    {
        // The seam declares "my state IS an account" (ADR-PC-033 slot 1). Degenerate: one balance,
        // no holds — so IAccount yes, the IHoldable transactional refinement deliberately NO (the
        // available/accounting split is trivially uniform with an empty hold set).
        Assert.IsAssignableFrom<IAccount>(LoanPosition.Empty);
        // Reflection (not an `is` pattern): the record is sealed, so the compiler would prove the
        // pattern always-false (CS0184) — which is exactly the degenerate posture we pin here.
        Assert.False(typeof(IHoldable).IsAssignableFrom(typeof(LoanPosition)),
            "a personal loan is a DEGENERATE account — it must not declare the transactional IHoldable refinement");
    }

    [Fact]
    public void AccountRef_is_the_loans_own_stream_id_after_the_disbursement_fold()
    {
        var loanId = Guid.NewGuid();
        var active = Fold(LoanPosition.Empty, Disbursed(loanId));

        IAccount account = active;
        // The account_ref is the loan's OWN opaque instance id — not the disbursement account (that
        // is the counterparty the disbursement moved money to) and never PII (ADR-PC-004 §P2).
        Assert.Equal(loanId.ToString(), account.AccountRef);
        Assert.False(string.IsNullOrEmpty(account.AccountRef));
        Assert.NotEqual(active.DisbursementAccountRef, account.AccountRef);
    }

    [Fact]
    public void AccountRef_is_stable_across_subsequent_folds()
    {
        var loanId = Guid.NewGuid();
        var active = Fold(LoanPosition.Empty, Disbursed(loanId));
        var refAtDisbursement = ((IAccount)active).AccountRef;

        var afterInstallment = Fold(active, new LoanInstallmentPaid(
            loanId, 1, new Money(5_000), new Money(81_066), new Money(918_934), new DateOnly(2026, 2, 1)));

        // The stream id never moves once folded, so the account_ref the movement ledger keys by is stable.
        Assert.Equal(refAtDisbursement, ((IAccount)afterInstallment).AccountRef);
    }

    [Fact]
    public void The_seam_leaves_the_record_equality_semantics_unchanged()
    {
        // AccountRef is a COMPUTED property over the already-folded LoanId, not a record positional
        // parameter, so the compiler-synthesised record equality (the byte-identical replay
        // determinism the engine relies on, ADR-PC-010 §P5) must behave exactly as before: two
        // independently-folded but identical positions are equal and hash identically.
        var loanId = Guid.NewGuid();
        var first = Fold(LoanPosition.Empty, Disbursed(loanId));
        var second = Fold(LoanPosition.Empty, Disbursed(loanId));

        Assert.Equal(first, second);
        Assert.Equal(first.GetHashCode(), second.GetHashCode());

        // And two DIFFERENT loans stay unequal — the seam added no equality wrinkle either way.
        var other = Fold(LoanPosition.Empty, Disbursed(Guid.NewGuid()));
        Assert.NotEqual(first, other);
    }

    // --- helpers ---

    private static LoanDisbursed Disbursed(Guid loanId) => new(
        LoanId: loanId,
        Principal: new Money(1_000_000),
        TanBasisPoints: 600,
        RateSheetVersionId: "rs-1",
        TermMonths: 12,
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
        // Mirrors LoanPositionFoldTests: fold through the family's OWN handler registry — the same
        // one the durable runtime folds through — so the seam is proved on real folded state.
        var name = @event.GetType().Name;
        var eventType = @event.GetType().Namespace == "Babelstone.Engine"
            ? $"operations.{name}"
            : $"personal_loan.{name}";
        Assert.True(Registry.TryResolve(eventType, out var handler), $"no handler for {eventType}");
        return (LoanPosition)handler.ApplyBoxed(state, @event).NewState;
    }
}
