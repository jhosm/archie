using Babelstone.Engine;
using Babelstone.Lifecycle;

namespace Babelstone.Families.PersonalLoan.Lifecycle;

/// <summary>
/// The personal-loan family's ONE LifecycleMilestone→command dispatch mapping (ADR-PC-036 §Decision 7).
/// In plain terms: "installment N of loan L is due on
/// D" must mean EXACTLY ONE wire command — same command kind, same number-pinned occurrence key, same
/// endpoint path, same body shape — whether it is the PRODUCTION driver firing it (the
/// <see cref="InstallmentRule"/> A4b path) or the engine's simulation FORECAST scheduling it (a
/// <c>SimulationRuntime</c> <see cref="LifecycleMilestone"/>). This static mapping is that single source:
/// both consumers derive their artifact from it, so the forecast is a cheap drift guard over what production
/// actually fires.
/// </summary>
/// <remarks>
/// The fitness function (<c>ADR-PC-036 §Decision 7</c>) compares the two derived artifacts — the production
/// rule's <see cref="LifecycleCommandDecision"/> against the forecast's identity-stamped
/// <see cref="LifecycleMilestone"/> for the same occurrence — and fails if they ever diverge (kind,
/// occurrence key, due instant, or the canonical dispatch id derived from them). Pure: no clock, no I/O —
/// the same inputs always map to the same command (ADR-PC-010 §P5).
/// </remarks>
public static class PersonalLoanLifecycleDispatch
{
    /// <summary>The STABLE command-kind the installment idempotency key is derived under. MUST equal the
    /// engine installment endpoint's own derivation kind (<c>LoansEndpoints.PayInstallmentCommandKind =
    /// "pay_installment"</c>) so the driver-derived id and the engine-derived id are identical (LCD-1,
    /// ADR-PC-036 §Decision 1+3) — named here, not referenced, to keep the family lifecycle contribution
    /// free of a family-application compile dependency.</summary>
    public const string CommandKindPayInstallment = "pay_installment";

    /// <summary>The scoped, non-interactive SCA service principal the loan installment money-mover route
    /// authorises the driver by (ADR-PC-036 §Decision 1). Kept in lock-step with
    /// the engine-side <c>ScaServicePrincipal.LifecycleMoneyMoverScope</c>; the token keeps its
    /// <c>deposit-money-mover</c> spelling for byte-for-byte lock-step with the gateway/IAM allowance — a
    /// FAMILY-NEUTRAL scope by MEANING even though the string still reads "deposit".</summary>
    public const string MoneyMoverScope = "lifecycle:deposit-money-mover";

    /// <summary>
    /// The ONE production command for "installment <paramref name="installmentNumber"/> of loan
    /// <paramref name="loanId"/> falls due on <paramref name="dueDate"/>" — the
    /// <see cref="LifecycleCommandDecision"/> the driver's recurring rule surfaces (ADR-PC-036
    /// §Decision 2/3). The occurrence key is the stable installment NUMBER, never the due-date — the
    /// number-pin the whole double-collection safety rests on (LCD-1).
    /// </summary>
    /// <param name="loanId">The loan aggregate/stream the command mutates.</param>
    /// <param name="installmentNumber">The STABLE installment number (the occurrence key).</param>
    /// <param name="dueDate">The installment's own due date — rides as <c>paid_at</c>, the business
    /// valid_time the engine stamps, so a late/backfilled firing records the correct date (ADR-PC-002).</param>
    /// <param name="collectionAccountRef">The loan's OPAQUE collection-account reference (the
    /// disbursement-account token) — never an IBAN, no PII (ADR-PC-004 §P2).</param>
    public static LifecycleCommandDecision PayInstallmentDecision(
        Guid loanId, long installmentNumber, DateOnly dueDate, string collectionAccountRef) =>
        new(
            InstanceId: loanId,
            CommandKind: CommandKindPayInstallment,
            OccurrenceKey: installmentNumber,
            RequestPath: $"/v1/loans/{loanId:D}/installment",
            // paid_at carries the installment's OWN due date as the business valid_time (ADR-PC-036
            // §Context; ADR-PC-002). Money is cents-native and no PII rides the body (ADR-PC-004 §P2).
            Body: new Dictionary<string, object?>
            {
                ["collection_account_ref"] = collectionAccountRef,
                ["paid_at"] = DueInstant(dueDate),
            },
            DueAt: dueDate,
            // The installment route is a clock-driven money-mover behind the shared SCA gate: the
            // non-interactive driver presents the SCOPED, gateway-attested service principal
            // (ADR-PC-036 §Decision 1) — the SAME scope the deposit maturity presents.
            ServicePrincipalScope: MoneyMoverScope);

    /// <summary>
    /// The SAME occurrence as a forecast milestone (ADR-PC-036 §Decision 7): the
    /// <see cref="LifecycleMilestone"/> a simulation's forward schedule carries for installment
    /// <paramref name="installmentNumber"/>, stamped with the production command identity
    /// (<see cref="LifecycleMilestone.CommandKind"/> / <see cref="LifecycleMilestone.OccurrenceKey"/>) and
    /// due at the SAME instant the production body's <c>paid_at</c> carries — so a forecast milestone and
    /// the production command for one occurrence are two views of ONE mapping, and the fitness test can
    /// fail on any divergence.
    /// </summary>
    /// <param name="installmentNumber">The stable installment number (the occurrence key).</param>
    /// <param name="dueDate">The installment's due date; the milestone falls due at its UTC midnight.</param>
    /// <param name="step">The REAL lifecycle command to run when the simulation clock reaches the milestone
    /// (a closure over the family's real pay-installment path — the closure carries the loan instance; the
    /// simulation never hand-fakes events).</param>
    public static LifecycleMilestone InstallmentMilestone(
        long installmentNumber, DateOnly dueDate,
        Func<DateTimeOffset, CancellationToken, Task> step) =>
        new(
            DueAt: DueInstant(dueDate),
            Step: step,
            CommandKind: CommandKindPayInstallment,
            OccurrenceKey: installmentNumber);

    /// <summary>The occurrence's due instant on the wire (ADR-PC-036 §Context): the <see cref="DateOnly"/>
    /// due date as UTC midnight — the shape the engine endpoint's <c>DateTimeOffset? PaidAt</c> binds and
    /// stamps the event's valid_time from, and the instant the forecast milestone falls due at.</summary>
    public static DateTimeOffset DueInstant(DateOnly dueDate) =>
        new(dueDate.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);

    /// <summary>The STABLE command-kind the disbursement re-attempt idempotency key is derived under. MUST
    /// equal the engine disbursement endpoint's own derivation kind so the driver-derived id and the
    /// engine-derived id are identical (LCD-1, ADR-PC-036 §Decision 1+3). Named here, not referenced, to
    /// keep the family lifecycle contribution free of a family-application compile dependency.</summary>
    public const string CommandKindDisburse = "disburse";

    /// <summary>Disbursement is the degenerate ONE-SHOT occurrence (exactly one per loan), so its stable
    /// occurrence key is the constant <c>1</c> (ADR-PC-036 §Decision 3) — a re-attempt re-fires under the
    /// same constant so the driver's dedupe and the engine's command_dedup collapse it to one landing.</summary>
    public const long DisbursementOccurrence = 1;

    /// <summary>
    /// The re-attempt command for a loan whose approved disbursement was held DISBURSEMENT-PENDING
    /// (ADR-PC-043 slot 5, bd babelstone-98mj.6): re-fire the disbursement endpoint under the ONE-SHOT
    /// occurrence key so the engine's <c>command_dedup</c> (and the ADR-PC-043 slot-4 intent key) collapse a
    /// late original apply and this re-attempt to exactly ONE landing — the loan cannot be double-disbursed.
    /// The re-attempt fires only when the destination is receivable again (the rule's projection-driven gate),
    /// so the disbursement lands rather than being re-held.
    /// </summary>
    /// <param name="loanId">The disbursement-pending loan stream the re-attempt targets.</param>
    /// <param name="disbursementAccountRef">The loan's OPAQUE disbursement-account reference — never an IBAN,
    /// no PII (ADR-PC-004 §P2).</param>
    /// <param name="startDate">The loan's own disbursement start date — rides as <c>disbursed_at</c>, so the
    /// re-attempt records the correct business valid_time (ADR-PC-002).</param>
    public static LifecycleCommandDecision DisbursementRetryDecision(
        Guid loanId, string disbursementAccountRef, DateOnly startDate) =>
        new(
            InstanceId: loanId,
            CommandKind: CommandKindDisburse,
            OccurrenceKey: DisbursementOccurrence,
            RequestPath: $"/v1/loans/{loanId:D}/disbursement",
            Body: new Dictionary<string, object?>
            {
                ["disbursement_account_ref"] = disbursementAccountRef,
                ["disbursed_at"] = DueInstant(startDate),
            },
            DueAt: startDate,
            ServicePrincipalScope: MoneyMoverScope);
}
