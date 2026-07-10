using Xunit;
using static Babelstone.Families.TermDeposit.LifecycleTransitions;

namespace Babelstone.Families.TermDeposit.Tests;

/// <summary>
/// F.3 (babelstone-29v8): the term-deposit lifecycle state machine — the one explicit,
/// auditable transition-legality table. These are pure unit tests (no engine, no Docker) over
/// <see cref="LifecycleTransitions.IsLegal"/>: they pin WHERE each transition is legal and that
/// every business-terminal state is closed to every BUSINESS transition. The command logic that
/// drives the downstream transitions (early termination F.4, renewal F.5, partial withdrawal F.12)
/// is NOT tested here — F.3 owns only the legality table, those flows hang off it.
///
/// <para><b>GDPR erasure is the one cross-cutting exception (bd babelstone-nzw6).</b> A
/// business-terminal deposit (Matured/Failed/Renewed/TerminatedEarly/TransferredToHeirs) is closed
/// to every <i>business</i> transition, but NOT to <see cref="Transition.Erase"/>: GDPR Article 17
/// is a regulatory obligation that must be able to reach a deposit that still holds the subject's
/// PII even after the business lifecycle has closed (a matured deposit still carries the customer's
/// data until erased). So terminality is defined here against the BUSINESS transitions; the single
/// regulatory <see cref="Transition.Erase"/> is tested separately. The truly-closed state is
/// <see cref="DepositLifecycle.Erased"/> — closed to everything, including a re-erasure (idempotent).</para>
/// </summary>
public sealed class LifecycleTransitionsTests
{
    // The business-terminal ("closed") states: no BUSINESS transition is legal FROM any of them.
    // They remain open to the one cross-cutting regulatory transition (Erase) — see ErasedStates /
    // the GDPR section below — because a closed deposit still holds the subject's PII until erased.
    private static readonly DepositLifecycle[] BusinessTerminal =
    [
        DepositLifecycle.Matured,
        DepositLifecycle.Failed,
        DepositLifecycle.Renewed,
        DepositLifecycle.TerminatedEarly,
        DepositLifecycle.TransferredToHeirs,
    ];

    // Every transition the table governs — the legality machine must answer for all of them.
    private static readonly Transition[] AllTransitions = Enum.GetValues<Transition>();

    // The BUSINESS transitions (everything except the cross-cutting regulatory Erase AND the
    // orthogonal undeliverable-payout recovery pair). Terminality is defined against THESE — a closed
    // deposit rejects every one of them. PayoutPend/LandPayout (ADR-PC-043 slot 5, bd babelstone-98mj.6)
    // are EXCLUDED for the same reason Erase is: they are a reversible marker orthogonal to the business
    // lifecycle (PayoutPend fires FROM the business-terminal Matured to hold an undeliverable payout at
    // source), so "terminal to normal business operations" and "still holdable/resolvable" coexist — the
    // payout-pending pair is tested separately below.
    private static readonly Transition[] BusinessTransitions =
        AllTransitions
            .Where(t => t is not Transition.Erase and not Transition.PayoutPend and not Transition.LandPayout)
            .ToArray();

    // ---- opening / rejecting: only from the seed Pending state -----------------------------------

    [Theory]
    [InlineData(Transition.Constitute)]
    [InlineData(Transition.FailConstitution)]
    public void Opening_a_deposit_is_legal_only_from_Pending(Transition opening)
    {
        Assert.True(IsLegal(DepositLifecycle.Pending, opening));

        // Not from Active (already open) nor from any terminal state (constitute-once).
        Assert.False(IsLegal(DepositLifecycle.Active, opening));
        foreach (var terminal in BusinessTerminal)
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
        // … and not once it has reached any business-terminal state.
        foreach (var terminal in BusinessTerminal)
        {
            Assert.False(IsLegal(terminal, transition));
        }
    }

    // ---- terminality: every closed state rejects every BUSINESS transition -----------------------

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

    // ---- undeliverable-payout hold: the reversible marker on the closed side of maturity ---------
    // ADR-PC-043 slot 5 / bd babelstone-98mj.6.

    [Fact]
    public void Payout_hold_is_legal_only_from_Matured_and_lands_only_from_PayoutPending()
    {
        // A matured deposit whose payout cannot land holds it at source (Matured → PayoutPending), and the
        // held payout lands only from PayoutPending (PayoutPending → Matured). Each leg fires from exactly
        // its one source state — the reversible marker, never from Active/Pending/another terminal.
        Assert.True(IsLegal(DepositLifecycle.Matured, Transition.PayoutPend));
        Assert.True(IsLegal(DepositLifecycle.PayoutPending, Transition.LandPayout));

        Assert.False(IsLegal(DepositLifecycle.Active, Transition.PayoutPend));
        Assert.False(IsLegal(DepositLifecycle.PayoutPending, Transition.PayoutPend));
        Assert.False(IsLegal(DepositLifecycle.Matured, Transition.LandPayout));
        Assert.False(IsLegal(DepositLifecycle.Active, Transition.LandPayout));
    }

    [Fact]
    public void PayoutPending_is_not_business_terminal_it_still_holds_pii_and_can_land()
    {
        // PayoutPending is a reversible, non-terminal marker: it can land (LandPayout) and — because the
        // deposit still holds the subject's PII while held — it is erasable (Erase). But it is NOT open to
        // the normal business transitions (accrue/mature/renew/…), which fire only from Active.
        Assert.True(IsLegal(DepositLifecycle.PayoutPending, Transition.LandPayout));
        Assert.True(IsLegal(DepositLifecycle.PayoutPending, Transition.Erase));
        foreach (var transition in BusinessTransitions)
        {
            Assert.False(
                IsLegal(DepositLifecycle.PayoutPending, transition),
                $"{transition} must be illegal from PayoutPending (a held-at-source marker, not a live deposit)");
        }
    }

    // ---- GDPR erasure: the one cross-cutting regulatory transition (bd babelstone-nzw6) -----------

    [Fact]
    public void Erasure_is_legal_from_every_state_that_still_holds_pii_live_or_closed()
    {
        // A live deposit and every business-terminal one still hold the subject's PII until erased —
        // GDPR Article 17 must reach them all (ADR-PC-004 §P3).
        Assert.True(IsLegal(DepositLifecycle.Active, Transition.Erase));
        foreach (var terminal in BusinessTerminal)
        {
            Assert.True(
                IsLegal(terminal, Transition.Erase),
                $"Erase must be legal from {terminal}: a closed deposit still holds the subject's PII until erased.");
        }
    }

    [Fact]
    public void Erasure_is_illegal_from_Pending_and_from_an_already_erased_deposit()
    {
        // Pending: no deposit exists to erase. Erased: already erased — re-erasure is rejected as an
        // illegal transition, which is also the idempotency guard (a second request is a no-op refusal).
        Assert.False(IsLegal(DepositLifecycle.Pending, Transition.Erase));
        Assert.False(IsLegal(DepositLifecycle.Erased, Transition.Erase));
    }

    [Fact]
    public void Erased_is_truly_closed_to_every_transition()
    {
        // The Erased state is the genuinely-terminal one: closed to EVERY transition, business or
        // regulatory — nothing operates on, reopens, or re-erases an erased deposit.
        foreach (var transition in AllTransitions)
        {
            Assert.False(
                IsLegal(DepositLifecycle.Erased, transition),
                $"{transition} must be illegal from the terminal Erased state");
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
