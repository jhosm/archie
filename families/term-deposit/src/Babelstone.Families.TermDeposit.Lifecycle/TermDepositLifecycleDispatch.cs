using Babelstone.Engine;
using Babelstone.Lifecycle;

namespace Babelstone.Families.TermDeposit.Lifecycle;

/// <summary>
/// The term-deposit family's ONE LifecycleMilestone→command dispatch mapping (ADR-PC-036 §Decision 7).
/// In plain terms: "deposit D matures on M" must mean
/// EXACTLY ONE wire command — same command kind, same one-shot occurrence key, same endpoint path, same body
/// shape — whether it is the PRODUCTION driver firing it (the <see cref="MaturityRule"/> A4a path) or the
/// engine's simulation FORECAST scheduling it (a <c>SimulationRuntime</c> <see cref="LifecycleMilestone"/>,
/// the A.8b forward lifecycle). This static mapping is that single source: both consumers derive their
/// artifact from it, so the forecast is a cheap drift guard over what production actually fires.
/// </summary>
/// <remarks>
/// The fitness function (<c>ADR-PC-036 §Decision 7</c>) compares the two derived artifacts — the production
/// rule's <see cref="LifecycleCommandDecision"/> against the forecast's identity-stamped
/// <see cref="LifecycleMilestone"/> for the same occurrence — and fails if they ever diverge (kind,
/// occurrence key, due instant, or the canonical dispatch id derived from them). Pure: no clock, no I/O —
/// the same inputs always map to the same command (ADR-PC-010 §P5).
/// </remarks>
public static class TermDepositLifecycleDispatch
{
    /// <summary>The STABLE command-kind the maturity idempotency key is derived under. MUST equal the engine
    /// maturity endpoint's own derivation kind (<c>DepositsEndpoints.MatureCommandKind = "mature"</c>) so the
    /// driver-derived id and the engine-derived id are identical (LCD-1, ADR-PC-036 §Decision 1+3) — named
    /// here, not referenced, to keep the family lifecycle contribution free of a family-application compile
    /// dependency.</summary>
    public const string CommandKindMature = "mature";

    /// <summary>The scoped, non-interactive SCA service principal the deposit money-mover route authorises
    /// the driver by (ADR-PC-036 §Decision 1). Kept in lock-step with the engine-side
    /// <c>ScaServicePrincipal.LifecycleMoneyMoverScope</c>; named locally (not referenced) so the family
    /// lifecycle contribution takes no dependency on the engine hosting assembly's SCA leaf.</summary>
    public const string MoneyMoverScope = "lifecycle:deposit-money-mover";

    /// <summary>Maturity is the degenerate ONE-SHOT occurrence (exactly one per deposit), so its stable
    /// occurrence key is the constant <c>1</c> (ADR-PC-036 §Decision 3) — the engine maturity endpoint
    /// derives its key under the same constant, and no settlement-health gate is needed (LCD-2 is a
    /// recurring-installment concern).</summary>
    public const long MaturityOccurrence = 1;

    /// <summary>
    /// The ONE production command for "deposit <paramref name="depositId"/> matures on
    /// <paramref name="maturityDate"/>" — the <see cref="LifecycleCommandDecision"/> the driver's one-shot
    /// rule surfaces (ADR-PC-036 §Decision 2/3/6).
    /// </summary>
    /// <param name="depositId">The deposit aggregate/stream the command mutates.</param>
    /// <param name="maturityDate">The deposit's own maturity date — rides as <c>matured_at</c>, the business
    /// valid_time the engine stamps, so a late/backfilled firing records the correct date (ADR-PC-002).</param>
    public static LifecycleCommandDecision MatureDecision(Guid depositId, DateOnly maturityDate) =>
        new(
            InstanceId: depositId,
            CommandKind: CommandKindMature,
            OccurrenceKey: MaturityOccurrence,
            RequestPath: $"/v1/deposits/{depositId:D}/maturity",
            // matured_at carries the deposit's OWN maturity date as the business valid_time (ADR-PC-036
            // §Context; ADR-PC-002). The payout account defaults engine-side; no PII rides the body
            // (ADR-PC-004 §P2).
            Body: new Dictionary<string, object?> { ["matured_at"] = DueInstant(maturityDate) },
            DueAt: maturityDate,
            ServicePrincipalScope: MoneyMoverScope);

    /// <summary>
    /// The SAME occurrence as a forecast milestone (ADR-PC-036 §Decision 7): the
    /// <see cref="LifecycleMilestone"/> a simulation's forward schedule (A.8b) carries for the deposit's
    /// maturity, stamped with the production command identity
    /// (<see cref="LifecycleMilestone.CommandKind"/> / <see cref="LifecycleMilestone.OccurrenceKey"/>) and
    /// due at the SAME instant the production body's <c>matured_at</c> carries — so a forecast milestone and
    /// the production command for one occurrence are two views of ONE mapping, and the fitness test can fail
    /// on any divergence.
    /// </summary>
    /// <param name="maturityDate">The deposit's maturity date; the milestone falls due at its UTC midnight.</param>
    /// <param name="step">The REAL lifecycle command to run when the simulation clock reaches the milestone
    /// (a closure over the family's real <c>MatureAsync</c> — the closure carries the deposit instance; the
    /// simulation never hand-fakes events).</param>
    public static LifecycleMilestone MaturityMilestone(
        DateOnly maturityDate, Func<DateTimeOffset, CancellationToken, Task> step) =>
        new(
            DueAt: DueInstant(maturityDate),
            Step: step,
            CommandKind: CommandKindMature,
            OccurrenceKey: MaturityOccurrence);

    /// <summary>The occurrence's due instant on the wire (ADR-PC-036 §Context): the <see cref="DateOnly"/>
    /// maturity date as UTC midnight — the shape the engine endpoint's <c>DateTimeOffset? MaturedAt</c>
    /// binds and stamps the event's valid_time from, and the instant the forecast milestone falls due at.</summary>
    public static DateTimeOffset DueInstant(DateOnly maturityDate) =>
        new(maturityDate.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);

    /// <summary>
    /// The re-attempt command for a deposit whose maturity payout was held PAYOUT-PENDING (ADR-PC-043 slot 5):
    /// re-fire the SAME maturity endpoint under the SAME one-shot occurrence key so the
    /// engine's <c>command_dedup</c> (and the ADR-PC-043 slot-4 intent key) collapse the re-attempt to exactly
    /// ONE landing — a late original apply and this re-attempt cannot double-pay. The re-attempt fires only
    /// when the destination is receivable again (the rule's projection-driven gate), so the payout lands
    /// rather than being re-held. Identical kind/occurrence/path/body to <see cref="MatureDecision"/>: it is
    /// the same economic occurrence, retried — NOT a new one.
    /// </summary>
    /// <param name="depositId">The payout-pending deposit stream the re-attempt targets.</param>
    /// <param name="maturityDate">The deposit's own maturity date — rides as <c>matured_at</c>, so the
    /// re-attempt records the correct business valid_time (ADR-PC-002).</param>
    public static LifecycleCommandDecision PayoutRetryDecision(Guid depositId, DateOnly maturityDate) =>
        MatureDecision(depositId, maturityDate);
}
