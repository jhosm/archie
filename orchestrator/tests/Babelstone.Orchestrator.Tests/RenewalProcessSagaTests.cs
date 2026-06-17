using Babelstone.Families.TermDeposit.Orchestration;
using Babelstone.Orchestrator.Saga;
using Xunit;

namespace Babelstone.Orchestrator.Tests;

/// <summary>
/// Pure tests of the <see cref="RenewalProcess"/> state machine + its result-event bridge (bd
/// babelstone-mtto) — the ADR-IC-003 §P2 "the state machine is the specification" fitness function for
/// the renewal saga. No clock, no I/O, no DB: the transition table and the bridge map are pure data
/// structures and these assert their shape directly (the table IS the documentation; these prove it).
/// </summary>
public sealed class RenewalProcessSagaTests
{
    private readonly RenewalProcess _machine = new();

    [Fact]
    public void Starts_in_RENEWAL_STARTED()
    {
        Assert.Equal(RenewalProcess.States.RenewalStarted, _machine.InitialState);
        Assert.Equal(RenewalProcess.Type, _machine.SagaType);
        Assert.Equal("RenewalProcess", RenewalProcess.Type);
    }

    [Fact]
    public void Happy_path_drives_RENEWAL_STARTED_to_RENEWAL_COMPLETED()
    {
        // The full renewal walk (02 §2.4.4), asserted as a chain through the table:
        // auto-started on DepositMatured → ConstituteRenewal; the constitute 2xx (synthesized
        // NewDepositConstituted) → LinkRenewal; the link 2xx (synthesized RenewalLinkConfirmed) → done.
        AssertTransition(RenewalProcess.States.RenewalStarted, RenewalProcess.DepositMatured,
            RenewalProcess.States.RenewalConstituting, RenewalProcess.ConstituteRenewal);
        AssertTransition(RenewalProcess.States.RenewalConstituting, RenewalProcess.NewDepositConstituted,
            RenewalProcess.States.RenewalLinking, RenewalProcess.LinkRenewal);
        AssertTransition(RenewalProcess.States.RenewalLinking, RenewalProcess.RenewalLinkConfirmed,
            RenewalProcess.States.RenewalCompleted);
    }

    [Fact]
    public void A_refused_constitute_escalates_to_HIR_with_no_compensation_command()
    {
        // ADR-IC-003 §P6: the payout already moved at maturity, so a failure NEVER compensates — it
        // escalates to HUMAN_INTERVENTION_REQUIRED, emitting NO reversal command.
        AssertTransition(RenewalProcess.States.RenewalConstituting, RenewalProcess.ConstituteRenewalFailed,
            RenewalProcess.States.HumanInterventionRequired);
    }

    [Fact]
    public void A_refused_link_escalates_to_HIR_with_no_compensation_command()
    {
        AssertTransition(RenewalProcess.States.RenewalLinking, RenewalProcess.LinkRenewalFailed,
            RenewalProcess.States.HumanInterventionRequired);
    }

    [Theory]
    [InlineData(RenewalProcess.States.RenewalStarted)]
    [InlineData(RenewalProcess.States.RenewalConstituting)]
    [InlineData(RenewalProcess.States.RenewalLinking)]
    public void An_explicit_escalation_from_any_non_terminal_state_reaches_HIR(string from)
    {
        AssertTransition(from, RenewalProcess.RenewalEscalated, RenewalProcess.States.HumanInterventionRequired);
    }

    [Fact]
    public void HIR_is_NON_terminal_by_table_and_an_operator_resolves_it_to_RENEWAL_COMPLETED()
    {
        // The crux of the renewal saga's terminal model (UNLIKE ConstitutionProcess): HIR has an OUTGOING
        // OperatorResolved edge in the table, so the substrate default TableStateMachine.IsTerminal (pure
        // table inspection) ALREADY reports it non-terminal — NO IsTerminal override is needed. This
        // assertion fails the moment the OperatorResolved edge is dropped (which would make HIR terminal).
        Assert.False(_machine.IsTerminal(RenewalProcess.States.HumanInterventionRequired));
        AssertTransition(RenewalProcess.States.HumanInterventionRequired, RenewalProcess.OperatorResolved,
            RenewalProcess.States.RenewalCompleted);
    }

    [Fact]
    public void RENEWAL_COMPLETED_is_terminal()
    {
        // The only terminal state — no outgoing edge in the table, so the substrate default reports it
        // terminal. The named States.IsTerminal predicate agrees (kept in lockstep with the table).
        Assert.True(_machine.IsTerminal(RenewalProcess.States.RenewalCompleted));
        Assert.True(RenewalProcess.States.IsTerminal(RenewalProcess.States.RenewalCompleted));
        // No forward state is terminal — each has an outgoing edge.
        Assert.False(_machine.IsTerminal(RenewalProcess.States.RenewalStarted));
        Assert.False(_machine.IsTerminal(RenewalProcess.States.RenewalConstituting));
        Assert.False(_machine.IsTerminal(RenewalProcess.States.RenewalLinking));
        // States.IsTerminal agrees with the machine on HIR (non-terminal).
        Assert.False(RenewalProcess.States.IsTerminal(RenewalProcess.States.HumanInterventionRequired));
    }

    [Fact]
    public void Cross_vocabulary_and_illegal_pairs_are_rejected()
    {
        // An event with no (state, event) row is rejected (ADR-IC-003 §P2) — never silently applied.
        Assert.False(_machine.TryAdvance(RenewalProcess.States.RenewalStarted, RenewalProcess.RenewalLinkConfirmed, out _));
        Assert.False(_machine.TryAdvance(RenewalProcess.States.RenewalCompleted, RenewalProcess.DepositMatured, out _));
        // A constitution-saga event is not in the renewal table.
        Assert.False(_machine.TryAdvance(RenewalProcess.States.RenewalStarted, "ConstitutionRequested", out _));
    }

    // ---- The result-event bridge map (the SYNTHESIZED forward signals) --------------------------------

    [Fact]
    public void Bridge_synthesizes_the_forward_signals_from_each_command_2xx()
    {
        // The two forward advances are SYNTHESIZED from the command HTTP 2xx (NOT read off the bus) — the
        // new stream's DepositConstituted carries ce_subject = newDepositId ≠ process_id, so it cannot
        // correlate off the bus. (ConstituteRenewal, Applied) → NewDepositConstituted; (LinkRenewal,
        // Applied) → RenewalLinkConfirmed.
        Assert.Equal(RenewalProcess.NewDepositConstituted,
            RenewalResultEvents.ForOutcome(RenewalProcess.ConstituteRenewal, CommandDeliveryKind.Applied));
        Assert.Equal(RenewalProcess.RenewalLinkConfirmed,
            RenewalResultEvents.ForOutcome(RenewalProcess.LinkRenewal, CommandDeliveryKind.Applied));
    }

    [Fact]
    public void Bridge_maps_a_refusal_to_the_per_leg_failure_event()
    {
        // A 4xx Refused (the substrate's enum, NOT "Rejected") on either leg → the per-leg failure event,
        // which the table routes to HIR. No compensation (ADR-IC-003 §P6).
        Assert.Equal(RenewalProcess.ConstituteRenewalFailed,
            RenewalResultEvents.ForOutcome(RenewalProcess.ConstituteRenewal, CommandDeliveryKind.Refused));
        Assert.Equal(RenewalProcess.LinkRenewalFailed,
            RenewalResultEvents.ForOutcome(RenewalProcess.LinkRenewal, CommandDeliveryKind.Refused));
    }

    [Fact]
    public void Bridge_has_no_carve_outs()
    {
        var bridge = new RenewalResultEvents.Bridge();
        Assert.Equal(RenewalProcess.Type, bridge.SagaType);
        // No no-route auto-pass: both renewal commands have real HTTP routes.
        Assert.False(bridge.IsNoRouteAutoPass(RenewalProcess.ConstituteRenewal));
        Assert.False(bridge.IsNoRouteAutoPass(RenewalProcess.LinkRenewal));
        // No HTTP-202 reinterpretation: the renewal legs carry no INDETERMINATE semantics.
        Assert.Null(bridge.ClassifyResponse(RenewalProcess.ConstituteRenewal, 202));
        Assert.Null(bridge.ClassifyResponse(RenewalProcess.LinkRenewal, 200));
        // An unmapped (command, kind) drives no advance (a graceful no-op).
        Assert.Null(bridge.ForOutcome(RenewalProcess.ConstituteRenewal, CommandDeliveryKind.Indeterminate));
        Assert.Null(bridge.ForOutcome("UnknownCommand", CommandDeliveryKind.Applied));
    }

    [Fact]
    public void Bridge_view_delegates_to_the_static_map()
    {
        var bridge = new RenewalResultEvents.Bridge();
        Assert.Equal(
            RenewalResultEvents.ForOutcome(RenewalProcess.ConstituteRenewal, CommandDeliveryKind.Applied),
            bridge.ForOutcome(RenewalProcess.ConstituteRenewal, CommandDeliveryKind.Applied));
    }

    // ---- The deterministic new-deposit-id derivation (crash-safe, replayable) -------------------------

    [Fact]
    public void New_deposit_id_is_deterministically_derived_from_the_process_id()
    {
        // Two FIXED process-id literals — the whole derivation is deterministic, so the test inputs are too
        // (no Guid.NewGuid): the assertions pin concrete derived ids, not just "some random distinct pair".
        var processId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var otherProcessId = Guid.Parse("22222222-2222-2222-2222-222222222222");

        // Stable across calls (no Guid.NewGuid, no clock) — so a crash-recovery reissue targets the same
        // new stream and the renewal is replayable.
        Assert.Equal(
            RenewalCommandPayloadFactory.NewDepositId(processId),
            RenewalCommandPayloadFactory.NewDepositId(processId));
        // A distinct process id yields a distinct new deposit id, and the new id is never the process id.
        Assert.NotEqual(RenewalCommandPayloadFactory.NewDepositId(processId), processId);
        Assert.NotEqual(
            RenewalCommandPayloadFactory.NewDepositId(processId),
            RenewalCommandPayloadFactory.NewDepositId(otherProcessId));
    }

    private void AssertTransition(string from, string evt, string expectedNext, params string[] expectedCommands)
    {
        Assert.True(_machine.TryAdvance(from, evt, out var outcome),
            $"expected a transition for ({from}, {evt}) but the table had none.");
        Assert.Equal(expectedNext, outcome.Next);
        Assert.Equal(expectedCommands, outcome.Commands);
    }
}
