namespace Babelstone.Lifecycle;

/// <summary>
/// The settlement-health predicate the RECURRING lifecycle path consults before firing occurrence N+1
/// (ADR-PC-036 §Decision 4, <c>LIFECYCLE_DRIVER_SETTLEMENT_HEALTH_GATE</c> / LCD-2). In plain terms: the
/// engine advances a loan's paid-count on the installment EVENT, not on settled CASH — the cash leg is a
/// downstream consequence (the ADR-PC-032 Originated <c>Movement</c>, effected by the substrate-owned
/// <c>SettlementProcess</c> saga). After an outage, an automated catch-up could therefore fire installment
/// N+1 while occurrence N's cash is still stuck in human intervention — advancing the schedule past money
/// actually collected. This probe is the driver's view of that cash leg: a recurring rule asks it "is this
/// instance's settlement parked?" and refuses to surface the next occurrence while the answer is yes.
/// </summary>
/// <remarks>
/// <para>
/// <b>A recurring-RULE concern, not generic-driver machinery.</b> The gate lives where the recurring
/// knowledge lives: the family's recurring rule (e.g. the personal-loan <c>InstallmentRule</c>) consults the
/// probe per due instance and simply does not surface the occurrence while the instance's cash leg is
/// parked. Maturity (one-shot) needs no gate (ADR-PC-036 §Decision 4) — the one-shot rules never consult
/// this. The probe itself stays FAMILY-AGNOSTIC (it keys on the instance/stream id alone, the same
/// <c>ce_subject</c> identity the settlement saga is keyed by), so it lives in the driver core without
/// naming a family (<c>LIFECYCLE_FAMILY_AGNOSTIC</c> holds).
/// </para>
/// <para>
/// <b>Held is correct behaviour; silent is not.</b> A permanently parked settlement stalls the instance's
/// schedule BY DESIGN (never advance paid-count past collected cash) — but that stall must be alerted, not
/// invisible (ADR-PC-036 §Residual risks). The alerting surface is an ops follow-up, outside this port.
/// </para>
/// <para>
/// The production implementation is <see cref="PostgresSettlementHealthProbe"/> — a read of the
/// orchestrator's <c>saga_state</c> row for the substrate-owned <c>SettlementProcess</c>. Tests substitute
/// a fake so the held/resume behaviour is provable without a live orchestrator.
/// </para>
/// </remarks>
public interface ISettlementHealthProbe
{
    /// <summary>
    /// Whether <paramref name="instanceId"/>'s de-settled cash leg (its ADR-PC-032 Originated
    /// <c>Movement</c>) is currently parked in <c>HUMAN_INTERVENTION_REQUIRED</c> — the
    /// <c>SettlementProcess</c> state an operator must resolve out of. <see langword="true"/> means the
    /// recurring path must NOT fire the instance's next occurrence this pass (it re-evaluates every tick, so
    /// the schedule resumes on the first pass after the leg settles). A probe failure should THROW, not
    /// guess healthy — the pass treats it as backpressure and retries, which fails CLOSED (money-safe).
    /// </summary>
    /// <param name="instanceId">The aggregate/stream whose cash-leg health is being asked — the SAME
    /// <c>ce_subject</c> identity the settlement saga instance is keyed by (ADR-IC-018 §P5).</param>
    /// <param name="ct">Cancellation propagated from the host's stopping token.</param>
    Task<bool> IsParkedAsync(Guid instanceId, CancellationToken ct = default);
}
