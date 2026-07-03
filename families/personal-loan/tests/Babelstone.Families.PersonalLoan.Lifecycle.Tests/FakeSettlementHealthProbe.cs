using Babelstone.Lifecycle;

namespace Babelstone.Families.PersonalLoan.Lifecycle.Tests;

/// <summary>
/// In-memory <see cref="ISettlementHealthProbe"/> test double (ADR-PC-036 §Decision 4, LCD-2): a settable
/// "is this instance's cash leg parked?" answer, so the gate's held/resume behaviour is provable with no
/// orchestrator database. <see cref="Park"/> models the settlement saga landing in
/// <c>HUMAN_INTERVENTION_REQUIRED</c>; <see cref="Resolve"/> models the operator resolving it
/// (<c>OperatorResolved</c> → <c>SETTLEMENT_COMPLETED</c>). The default answer is healthy.
/// </summary>
internal sealed class FakeSettlementHealthProbe : ISettlementHealthProbe
{
    private readonly HashSet<Guid> _parked = [];

    /// <summary>Model the instance's cash leg parking in <c>HUMAN_INTERVENTION_REQUIRED</c>.</summary>
    public void Park(Guid instanceId) => _parked.Add(instanceId);

    /// <summary>Model the operator resolving the parked leg (the saga leaves the parked state).</summary>
    public void Resolve(Guid instanceId) => _parked.Remove(instanceId);

    public Task<bool> IsParkedAsync(Guid instanceId, CancellationToken ct = default) =>
        Task.FromResult(_parked.Contains(instanceId));
}
