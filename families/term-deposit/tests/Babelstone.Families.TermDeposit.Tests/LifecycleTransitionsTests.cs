using Xunit;
using static Babelstone.Families.TermDeposit.LifecycleTransitions;

namespace Babelstone.Families.TermDeposit.Tests;

/// <summary>
/// F.3 (babelstone-29v8): the term-deposit lifecycle state machine — the one explicit,
/// auditable transition-legality table. These are pure unit tests (no engine, no Docker) over
/// <see cref="LifecycleTransitions.IsLegal"/>: they pin WHERE each transition is legal and that
/// every terminal state is closed to every transition. The command logic that drives the
/// downstream transitions (early termination F.4, renewal F.5, partial withdrawal F.12) is NOT
/// tested here — F.3 owns only the legality table, those flows hang off it.
/// </summary>
public sealed class LifecycleTransitionsTests
{
    // The terminal ("closed") states: no transition is legal FROM any of them.
    private static readonly DepositLifecycle[] Terminal =
    [
        DepositLifecycle.Matured,
        DepositLifecycle.Failed,
        DepositLifecycle.Renewed,
        DepositLifecycle.TerminatedEarly,
        DepositLifecycle.TransferredToHeirs,
    ];

    // Every transition the table governs — the legality machine must answer for all of them.
    private static readonly Transition[] AllTransitions = Enum.GetValues<Transition>();

    // ---- opening / rejecting: only from the seed Pending state -----------------------------------

    [Theory]
    [InlineData(Transition.Constitute)]
    [InlineData(Transition.FailConstitution)]
    public void Opening_a_deposit_is_legal_only_from_Pending(Transition opening)
    {
        Assert.True(IsLegal(DepositLifecycle.Pending, opening));

        // Not from Active (already open) nor from any terminal state (constitute-once).
        Assert.False(IsLegal(DepositLifecycle.Active, opening));
        foreach (var terminal in Terminal)
        {
            Assert.False(IsLegal(terminal, opening));
        }
    }

    // ---- operating + closing: only from Active ---------------------------------------------------

    [Theory]
    [InlineData(Transition.AccrueInterest)]
    [InlineData(Transition.ApplyWithholding)]
    [InlineData(Transition.PayInterest)]
    [InlineData(Transition.PartiallyWithdraw)]
    [InlineData(Transition.Correct)]
    [InlineData(Transition.Mature)]
    [InlineData(Transition.Renew)]
    [InlineData(Transition.TerminateEarly)]
    [InlineData(Transition.TransferToHeirs)]
    public void Operating_and_closing_transitions_are_legal_only_from_Active(Transition transition)
    {
        Assert.True(IsLegal(DepositLifecycle.Active, transition));

        // Not before the deposit exists …
        Assert.False(IsLegal(DepositLifecycle.Pending, transition));
        // … and not once it has reached any terminal state.
        foreach (var terminal in Terminal)
        {
            Assert.False(IsLegal(terminal, transition));
        }
    }

    // ---- terminality: every closed state rejects every transition --------------------------------

    [Fact]
    public void Every_terminal_state_is_closed_to_every_transition()
    {
        foreach (var terminal in Terminal)
        {
            foreach (var transition in AllTransitions)
            {
                Assert.False(
                    IsLegal(terminal, transition),
                    $"{transition} must be illegal from terminal state {terminal}");
            }
        }
    }

    // ---- the canonical illegal commands the issue calls out --------------------------------------

    [Fact]
    public void Maturing_a_matured_deposit_is_illegal()
        => Assert.False(IsLegal(DepositLifecycle.Matured, Transition.Mature));

    [Fact]
    public void Paying_a_coupon_on_a_closed_deposit_is_illegal()
    {
        Assert.False(IsLegal(DepositLifecycle.Matured, Transition.PayInterest));
        Assert.False(IsLegal(DepositLifecycle.TerminatedEarly, Transition.PayInterest));
        Assert.False(IsLegal(DepositLifecycle.TransferredToHeirs, Transition.PayInterest));
        Assert.False(IsLegal(DepositLifecycle.Renewed, Transition.PayInterest));
    }

    // ---- completeness: the table answers for every transition × every state ----------------------

    [Fact]
    public void IsLegal_answers_for_every_transition_from_every_state_without_throwing()
    {
        foreach (var state in Enum.GetValues<DepositLifecycle>())
        {
            foreach (var transition in AllTransitions)
            {
                // No transition is ever legal from more than the states named above; the point here
                // is that the lookup is TOTAL — it never throws on a missing row, it returns false.
                _ = IsLegal(state, transition);
            }
        }
    }
}
