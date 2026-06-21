using Xunit;
using static Babelstone.Families.CreditoPessoal.LifecycleTransitions;

namespace Babelstone.Families.CreditoPessoal.Tests;

/// <summary>
/// The credito_pessoal lifecycle state machine — the one explicit, auditable transition-legality table.
/// Pure unit tests (no engine, no Docker) over <see cref="LifecycleTransitions.IsLegal"/>: they pin WHERE
/// each transition is legal and that every business-terminal state is closed to every BUSINESS transition.
/// Mirrors the term-deposit family's lifecycle tests, including the GDPR-erasure cross-cutting exception.
/// </summary>
public sealed class LifecycleTransitionsTests
{
    // The business-terminal ("closed") states: no BUSINESS transition is legal FROM any of them. They
    // remain open to the one cross-cutting regulatory transition (Erase) — a closed loan still holds the
    // subject's PII until erased.
    private static readonly LoanLifecycle[] BusinessTerminal =
    [
        LoanLifecycle.Failed,
        LoanLifecycle.Settled,
        LoanLifecycle.WrittenOff,
    ];

    private static readonly Transition[] AllTransitions = Enum.GetValues<Transition>();

    // The BUSINESS transitions (everything except the cross-cutting regulatory Erase).
    private static readonly Transition[] BusinessTransitions =
        AllTransitions.Where(t => t != Transition.Erase).ToArray();

    [Theory]
    [InlineData(Transition.Disburse)]
    [InlineData(Transition.FailDisbursement)]
    public void Opening_a_loan_is_legal_only_from_Pending(Transition opening)
    {
        Assert.True(IsLegal(LoanLifecycle.Pending, opening));

        // Not from Active (already open) nor from any terminal state (disburse-once).
        Assert.False(IsLegal(LoanLifecycle.Active, opening));
        foreach (var terminal in BusinessTerminal)
        {
            Assert.False(IsLegal(terminal, opening));
        }
    }

    [Theory]
    [InlineData(Transition.PayInstallment)]
    [InlineData(Transition.RepayEarly)]
    [InlineData(Transition.Settle)]
    [InlineData(Transition.WriteOff)]
    public void Operating_and_closing_transitions_are_legal_only_from_Active(Transition transition)
    {
        Assert.True(IsLegal(LoanLifecycle.Active, transition));

        // Not before the loan exists …
        Assert.False(IsLegal(LoanLifecycle.Pending, transition));
        // … and not once it has reached any business-terminal state.
        foreach (var terminal in BusinessTerminal)
        {
            Assert.False(IsLegal(terminal, transition));
        }
    }

    [Fact]
    public void Every_business_terminal_state_is_closed_to_every_business_transition()
    {
        foreach (var terminal in BusinessTerminal)
        {
            foreach (var transition in BusinessTransitions)
            {
                Assert.False(
                    IsLegal(terminal, transition),
                    $"{transition} must be illegal from business-terminal state {terminal}");
            }
        }
    }

    [Fact]
    public void Erasure_is_legal_from_every_state_that_still_holds_pii_live_or_closed()
    {
        // A live loan and every business-terminal one still hold the subject's PII until erased — GDPR
        // Article 17 must reach them all (ADR-PC-004 §P3).
        Assert.True(IsLegal(LoanLifecycle.Active, Transition.Erase));
        foreach (var terminal in BusinessTerminal)
        {
            Assert.True(
                IsLegal(terminal, Transition.Erase),
                $"Erase must be legal from {terminal}: a closed loan still holds the subject's PII until erased.");
        }
    }

    [Fact]
    public void Erasure_is_illegal_from_Pending_and_from_an_already_erased_loan()
    {
        // Pending: no loan exists to erase. Erased: already erased — re-erasure is rejected (the
        // idempotency guard).
        Assert.False(IsLegal(LoanLifecycle.Pending, Transition.Erase));
        Assert.False(IsLegal(LoanLifecycle.Erased, Transition.Erase));
    }

    [Fact]
    public void Erased_is_truly_closed_to_every_transition()
    {
        foreach (var transition in AllTransitions)
        {
            Assert.False(
                IsLegal(LoanLifecycle.Erased, transition),
                $"{transition} must be illegal from the terminal Erased state");
        }
    }

    [Fact]
    public void Settling_a_settled_loan_is_illegal()
        => Assert.False(IsLegal(LoanLifecycle.Settled, Transition.Settle));

    [Fact]
    public void Paying_an_installment_on_a_closed_loan_is_illegal()
    {
        Assert.False(IsLegal(LoanLifecycle.Settled, Transition.PayInstallment));
        Assert.False(IsLegal(LoanLifecycle.WrittenOff, Transition.PayInstallment));
        Assert.False(IsLegal(LoanLifecycle.Failed, Transition.PayInstallment));
    }

    [Fact]
    public void IsLegal_answers_for_every_transition_from_every_state_without_throwing()
    {
        foreach (var state in Enum.GetValues<LoanLifecycle>())
        {
            foreach (var transition in AllTransitions)
            {
                _ = IsLegal(state, transition); // total lookup — never throws on a missing row
            }
        }
    }
}
