using Babelstone.Engine;
using Babelstone.EventStore;

namespace Babelstone.Families.PersonalLoan;

/// <summary>
/// The personal_loan family's projection declarations (two-modes §5.4: declared in the family, not
/// hardcoded in the engine). Three projections: the loan POSITION (folded over the family's own handlers,
/// the live amortizing state), the AMORTIZATION SCHEDULE (as actually paid — the backward timeline), and
/// the INSTALLMENT CALENDAR (the forward next-unpaid installment).
/// Every v1 projection is <see cref="ProjectionMode.Async"/>. The engine spine never names this family;
/// the host composes infra + this declaration (ADR-PC-021 §D4).
/// </summary>
/// <remarks>
/// The loan-position runner reuses the family's own folds (the same
/// <see cref="PersonalLoanFamilyModule.Registry"/> the durable runtime uses), so the position
/// materialised into the bitemporal table is the SAME fold the live read path computes. The
/// amortization-schedule and installment-calendar projections each have their OWN state type, seed, and a
/// dedicated <see cref="HandlerRegistry"/> of folds that ignore every event type they do not record (the
/// runner skips an unhandled type, leaving the belief unchanged). Each is its own current-belief row per
/// <c>(stream_id, projection_kind)</c>. All three folds are pure and record the COMPUTED facts the events
/// carry (never recomputing the amortization split from a rate); the schedule accumulates append-only and
/// the calendar advances a forward pointer (a MAX over paid installment numbers), so a cold rebuild
/// reproduces each belief byte-for-byte (ADR-PC-010 §P5).
/// </remarks>
public sealed class PersonalLoanProjectionModule : IProjectionModule
{
    /// <summary>The family-prefixed discriminator for the loan-position projection.</summary>
    public const string LoanPositionKind = "personal_loan.loan_position";

    /// <summary>The family-prefixed discriminator for the amortization-schedule projection.</summary>
    public const string AmortizationScheduleKind = "personal_loan.amortization_schedule";

    /// <summary>The family-prefixed discriminator for the installment-calendar projection.</summary>
    public const string InstallmentCalendarKind = "personal_loan.installment_calendar";

    public string FamilyName => "personal_loan";

    public IReadOnlyList<IProjectionRunner> CreateRunners(ProjectionInfra infra)
    {
        var loanPosition = new ProjectionRunner<LoanPosition>(
            kind: LoanPositionKind,
            family: FamilyName,
            mode: ProjectionMode.Async,
            handlers: PersonalLoanFamilyModule.Registry(),
            serializer: infra.EventSerializer,
            seed: () => LoanPosition.Empty,
            store: new ProjectionStore<LoanPosition>(infra.Storage, new JsonStateSerializer<LoanPosition>()));

        var amortizationSchedule = new ProjectionRunner<AmortizationScheduleProjection>(
            kind: AmortizationScheduleKind,
            family: FamilyName,
            mode: ProjectionMode.Async,
            handlers: AmortizationScheduleRegistry(),
            serializer: infra.EventSerializer,
            seed: () => AmortizationScheduleProjection.Empty,
            store: new ProjectionStore<AmortizationScheduleProjection>(
                infra.Storage, new JsonStateSerializer<AmortizationScheduleProjection>()));

        var installmentCalendar = new ProjectionRunner<InstallmentCalendarProjection>(
            kind: InstallmentCalendarKind,
            family: FamilyName,
            mode: ProjectionMode.Async,
            handlers: InstallmentCalendarRegistry(),
            serializer: infra.EventSerializer,
            seed: () => InstallmentCalendarProjection.Empty,
            store: new ProjectionStore<InstallmentCalendarProjection>(
                infra.Storage, new JsonStateSerializer<InstallmentCalendarProjection>()));

        return [loanPosition, amortizationSchedule, installmentCalendar];
    }

    /// <summary>
    /// The amortization-schedule fold registry: only the balance-changing event types (the runner skips
    /// every other family event, leaving the schedule unchanged). Exposed so tests fold the same bindings
    /// the runner uses.
    /// </summary>
    public static HandlerRegistry AmortizationScheduleRegistry() => new(
    [
        new("personal_loan.LoanInstallmentPaid", typeof(LoanInstallmentPaid),
            new DispatchableHandler<AmortizationScheduleProjection, LoanInstallmentPaid>(
                new AmortizationScheduleInstallmentHandler())),
        new("personal_loan.LoanRepaidEarly", typeof(LoanRepaidEarly),
            new DispatchableHandler<AmortizationScheduleProjection, LoanRepaidEarly>(
                new AmortizationScheduleEarlyRepaymentHandler())),
    ]);

    /// <summary>
    /// The installment-calendar fold registry: only the two event types that move the FORWARD schedule —
    /// <see cref="LoanDisbursed"/> (fixes the schedule) and <see cref="LoanInstallmentPaid"/> (advances the
    /// paid count). The runner skips every other family event, leaving the forward pointer unchanged.
    /// Exposed so tests fold the same bindings the runner uses.
    /// </summary>
    public static HandlerRegistry InstallmentCalendarRegistry() => new(
    [
        new("personal_loan.LoanDisbursed", typeof(LoanDisbursed),
            new DispatchableHandler<InstallmentCalendarProjection, LoanDisbursed>(
                new InstallmentCalendarDisbursedHandler())),
        new("personal_loan.LoanInstallmentPaid", typeof(LoanInstallmentPaid),
            new DispatchableHandler<InstallmentCalendarProjection, LoanInstallmentPaid>(
                new InstallmentCalendarInstallmentPaidHandler())),
    ]);
}
