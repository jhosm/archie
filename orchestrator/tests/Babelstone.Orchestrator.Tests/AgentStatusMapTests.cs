using System.Reflection;
using Babelstone.Families.TermDeposit.Orchestration;
using Babelstone.Orchestrator.Saga;
using Xunit;

namespace Babelstone.Orchestrator.Tests;

/// <summary>
/// Lock tests for the family-owned <see cref="ISagaAgentStatusMap"/> projections (bd babelstone-vjoi /
/// Document 11 Pattern 2). These guard the invariant the agent-status endpoint depends on: the map is
/// TOTAL over its saga's state set, and the coarse <see cref="AgentStatus"/> it projects is CONSISTENT with
/// the machine's terminality answer (a terminal state ⇒ a terminal status, and vice versa). A state added to
/// a saga's vocabulary (ADR-IC-018 §D3) without a mapping arm makes <c>StatusFor</c> throw — caught here
/// before it can surface as a fail-closed 500 at the edge, the same lockstep discipline that keeps the
/// terminality predicate honest.
/// </summary>
public sealed class AgentStatusMapTests
{
    private static readonly string[] AllAgentStatuses =
    [
        AgentStatus.Processing, AgentStatus.AwaitingApproval, AgentStatus.ActionRequired,
        AgentStatus.Completed, AgentStatus.Failed, AgentStatus.Cancelled,
    ];

    private static readonly string[] TerminalStatuses =
        [AgentStatus.Completed, AgentStatus.Failed, AgentStatus.Cancelled];

    private static readonly string[] NonTerminalStatuses =
        [AgentStatus.Processing, AgentStatus.AwaitingApproval, AgentStatus.ActionRequired];

    // Reflect every `public const string` of a saga's nested `States` class — the full state vocabulary
    // (ADR-IC-018 §D3) — so a new state automatically joins the lock without editing the test.
    private static IEnumerable<string> StatesOf(Type statesType) =>
        statesType.GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!);

    public static TheoryData<string> ConstitutionStates()
    {
        var data = new TheoryData<string>();
        foreach (var s in StatesOf(typeof(ConstitutionProcess.States)))
        {
            data.Add(s);
        }

        return data;
    }

    public static TheoryData<string> RenewalStates()
    {
        var data = new TheoryData<string>();
        foreach (var s in StatesOf(typeof(RenewalProcess.States)))
        {
            data.Add(s);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(ConstitutionStates))]
    public void Constitution_map_is_total_and_consistent_with_terminality(string state)
    {
        var map = new ConstitutionProcessAgentStatusMap();
        var machine = new ConstitutionProcess();

        var status = map.StatusFor(state); // throws if a state has no arm — the totality lock
        Assert.Contains(status, AllAgentStatuses);
        Assert.Contains(status, machine.IsTerminal(state) ? TerminalStatuses : NonTerminalStatuses);
    }

    [Theory]
    [MemberData(nameof(RenewalStates))]
    public void Renewal_map_is_total_and_consistent_with_terminality(string state)
    {
        var map = new RenewalProcessAgentStatusMap();
        var machine = new RenewalProcess();

        var status = map.StatusFor(state);
        Assert.Contains(status, AllAgentStatuses);
        if (machine.IsTerminal(state))
        {
            // The renewal saga's only terminal state is success (money already moved at maturity, so
            // failures escalate rather than reach a terminal failure/cancel — ADR-IC-003 §P6).
            Assert.Equal(AgentStatus.Completed, status);
        }
        else
        {
            Assert.Contains(status, new[] { AgentStatus.Processing, AgentStatus.ActionRequired });
        }
    }

    [Fact]
    public void Constitution_map_pins_the_load_bearing_states()
    {
        var map = new ConstitutionProcessAgentStatusMap();

        Assert.Equal(AgentStatus.Processing, map.StatusFor(ConstitutionProcess.States.ParallelValidation));
        Assert.Equal(AgentStatus.AwaitingApproval, map.StatusFor(ConstitutionProcess.States.AwaitWorkflowApproval));
        Assert.Equal(AgentStatus.ActionRequired, map.StatusFor(ConstitutionProcess.States.HumanInterventionRequired));
        Assert.Equal(AgentStatus.Completed, map.StatusFor(ConstitutionProcess.States.Completed));
        Assert.Equal(AgentStatus.Failed, map.StatusFor(ConstitutionProcess.States.DepositConstitutionFailed));
        Assert.Equal(AgentStatus.Cancelled, map.StatusFor(ConstitutionProcess.States.Cancelled));
        Assert.Equal(AgentStatus.Cancelled, map.StatusFor(ConstitutionProcess.States.CancelledAfterDebit));
    }

    [Fact]
    public void Unknown_state_throws_so_a_new_state_cannot_silently_default()
    {
        var map = new ConstitutionProcessAgentStatusMap();
        Assert.Throws<ArgumentOutOfRangeException>(() => map.StatusFor("A_STATE_THAT_DOES_NOT_EXIST"));
    }

    [Fact]
    public void Each_map_governs_its_machines_saga_type()
    {
        Assert.Equal(new ConstitutionProcess().SagaType, new ConstitutionProcessAgentStatusMap().SagaType);
        Assert.Equal(new RenewalProcess().SagaType, new RenewalProcessAgentStatusMap().SagaType);
    }
}
