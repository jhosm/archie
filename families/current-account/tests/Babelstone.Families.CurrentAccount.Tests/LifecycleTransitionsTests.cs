using Xunit;
using static Babelstone.Families.CurrentAccount.LifecycleTransitions;

namespace Babelstone.Families.CurrentAccount.Tests;

/// <summary>
/// The current_account lifecycle state machine (ADR-PC-037) — the one explicit, auditable
/// transition-legality table. Pure unit tests (no engine, no Docker) over
/// <see cref="LifecycleTransitions.IsLegal"/>: they pin WHERE each transition is legal, the reversible
/// <c>Dormant ⇄ Active</c> pair that distinguishes the demand account from the loan's good-or-closed
/// binary, that every business-terminal state is closed to every BUSINESS transition, and the
/// GDPR-erasure cross-cutting exception.
/// </summary>
public sealed class LifecycleTransitionsTests
{
    // The business-terminal ("closed") states: no BUSINESS transition is legal FROM either. They remain
    // open to the one cross-cutting regulatory transition (Erase) — a closed/failed account still holds
    // the subject's PII until erased.
    private static readonly AccountLifecycle[] BusinessTerminal =
    [
        AccountLifecycle.Failed,
        AccountLifecycle.Closed,
    ];

    private static readonly Transition[] AllTransitions = Enum.GetValues<Transition>();

    // The BUSINESS transitions (everything except the cross-cutting regulatory Erase).
    private static readonly Transition[] BusinessTransitions =
        AllTransitions.Where(t => t != Transition.Erase).ToArray();

    [Theory]
    [InlineData(Transition.Open)]
    [InlineData(Transition.FailOpening)]
    public void Opening_an_account_is_legal_only_from_Pending(Transition opening)
    {
        Assert.True(IsLegal(AccountLifecycle.Pending, opening));

        // Not from Active (already open), Dormant, nor any terminal state (open-once).
        Assert.False(IsLegal(AccountLifecycle.Active, opening));
        Assert.False(IsLegal(AccountLifecycle.Dormant, opening));
        foreach (var terminal in BusinessTerminal)
        {
            Assert.False(IsLegal(terminal, opening));
        }
    }

    [Theory]
    [InlineData(Transition.MarkDormant)]
    [InlineData(Transition.Close)]
    public void Operating_and_closing_transitions_are_legal_only_from_Active(Transition transition)
    {
        Assert.True(IsLegal(AccountLifecycle.Active, transition));

        // Not before the account exists, not from Dormant (§D2: operating runs only from Active) …
        Assert.False(IsLegal(AccountLifecycle.Pending, transition));
        Assert.False(IsLegal(AccountLifecycle.Dormant, transition));
        // … and not once it has reached any business-terminal state.
        foreach (var terminal in BusinessTerminal)
        {
            Assert.False(IsLegal(terminal, transition));
        }
    }

    [Fact]
    public void The_dormant_reactivate_pair_is_reversible_and_only_between_its_two_states()
    {
        // MarkDormant: Active → Dormant; Reactivate: Dormant → Active. The reversible pair (ADR-PC-037
        // §D2), the distinguishing feature vs the loan's terminal-only close.
        Assert.True(IsLegal(AccountLifecycle.Active, Transition.MarkDormant));
        Assert.True(IsLegal(AccountLifecycle.Dormant, Transition.Reactivate));

        // Reactivation is legal ONLY from Dormant — not from Active (already active) or anywhere else.
        Assert.False(IsLegal(AccountLifecycle.Active, Transition.Reactivate));
        Assert.False(IsLegal(AccountLifecycle.Pending, Transition.Reactivate));
        foreach (var terminal in BusinessTerminal)
        {
            Assert.False(IsLegal(terminal, Transition.Reactivate));
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
        // A live (Active/Dormant) account and every business-terminal one still hold the subject's PII
        // until erased — GDPR Article 17 must reach them all (ADR-PC-004 §P3).
        Assert.True(IsLegal(AccountLifecycle.Active, Transition.Erase));
        Assert.True(IsLegal(AccountLifecycle.Dormant, Transition.Erase));
        foreach (var terminal in BusinessTerminal)
        {
            Assert.True(
                IsLegal(terminal, Transition.Erase),
                $"Erase must be legal from {terminal}: a closed account still holds the subject's PII until erased.");
        }
    }

    [Fact]
    public void Erasure_is_illegal_from_Pending_and_from_an_already_erased_account()
    {
        // Pending: no account exists to erase. Erased: already erased — re-erasure is rejected (the
        // idempotency guard).
        Assert.False(IsLegal(AccountLifecycle.Pending, Transition.Erase));
        Assert.False(IsLegal(AccountLifecycle.Erased, Transition.Erase));
    }

    [Fact]
    public void Erased_is_truly_closed_to_every_transition()
    {
        foreach (var transition in AllTransitions)
        {
            Assert.False(
                IsLegal(AccountLifecycle.Erased, transition),
                $"{transition} must be illegal from the terminal Erased state");
        }
    }

    [Fact]
    public void IsLegal_answers_for_every_transition_from_every_state_without_throwing()
    {
        foreach (var state in Enum.GetValues<AccountLifecycle>())
        {
            foreach (var transition in AllTransitions)
            {
                _ = IsLegal(state, transition); // total lookup — never throws on a missing row
            }
        }
    }
}
