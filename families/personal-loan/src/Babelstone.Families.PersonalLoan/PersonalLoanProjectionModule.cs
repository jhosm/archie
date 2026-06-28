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

    /// <summary>
    /// The family-prefixed discriminator for the denormalized CQRS installment-calendar READ MODEL
    /// (ADR-IC-005, bd babelstone-6cpq.12). DISTINCT from <see cref="InstallmentCalendarKind"/>: that one
    /// is the bitemporal belief in the generic <c>projections</c> table (a point lookup); this one feeds
    /// the flat, family-owned <c>read_model.installment_calendar</c> table the "installments due in
    /// [from, to)" range scan reads.
    /// </summary>
    public const string InstallmentReadModelKind = "personal_loan.installment_calendar_read_model";

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
    /// Builds the family's CQRS installment-calendar read-model runner (ADR-IC-005, bd babelstone-6cpq.12):
    /// folds the SAME <see cref="LoanPosition"/> the live read path computes and maps it to the family-owned
    /// <see cref="InstallmentCalendarReadModelRow"/> written to <c>read_model.installment_calendar</c>.
    /// Declared separately from <see cref="CreateRunners"/> because the flat read model is a distinct surface
    /// from the bitemporal <c>projections</c> store — distinct store, distinct rebuild discipline
    /// (truncate-and-refold, not supersede-all). Async (the v1 default), so it rides the existing
    /// drainer/relay unchanged. The runner is closed over BOTH the family's state type AND its row type, so
    /// the engine spine never names a loan (ADR-PC-021 §D2/§P2). Mirrors term-deposit's
    /// <c>CreateReadModelRunner</c> one-for-one over <see cref="LoanPosition"/>.
    /// </summary>
    public IProjectionRunner CreateReadModelRunner(ReadModelInfra<InstallmentCalendarReadModelRow> infra) =>
        new ReadModelRunner<LoanPosition, InstallmentCalendarReadModelRow>(
            kind: InstallmentReadModelKind,
            family: FamilyName,
            mode: ProjectionMode.Async,
            handlers: PersonalLoanFamilyModule.Registry(),
            serializer: infra.EventSerializer,
            seed: () => LoanPosition.Empty,
            map: MapToReadModel,
            store: infra.Store);

    /// <summary>
    /// The pure state→row mapper (no clock, no I/O): projects the folded <see cref="LoanPosition"/> into the
    /// denormalized installment-calendar read-model row. The forward NEXT-unpaid occurrence is surfaced ONLY
    /// for a LIVE (<see cref="LoanLifecycle.Active"/>) loan that still owes an installment
    /// (<c>InstallmentsPaid &lt; TermMonths</c>): occurrence <c>InstallmentsPaid + 1</c>, its due date the
    /// schedule anchor rolled forward by the paid count (deterministic date arithmetic on event-carried
    /// dates, never a clock). A terminal loan (settled, written-off, erased, failed) or a fully-paid one
    /// surfaces NO occurrence — both the number and the due date go <see langword="null"/>, so it is excluded
    /// from the "installments due in [from, to)" range scan by construction.
    /// <c>sor = "engine"</c> for every engine-materialised loan (ADR-PC-018 §6.2, set at disbursement).
    /// <see cref="InstallmentCalendarReadModelRow.LastUpdated"/> is the producing event's transaction_time
    /// (event-derived, never the wall clock), so a rebuild is byte-identical (ADR-PC-010 §P5). The
    /// <see cref="InstallmentCalendarReadModelRow.Detail"/> body is the full structural state, serialized
    /// with the same deterministic JSON codec the bitemporal projection uses — no PII (ADR-PC-004 §P2). All
    /// money is integer cents (ADR-PC-010 §P1).
    /// </summary>
    public static InstallmentCalendarReadModelRow MapToReadModel(ReadModelFold<LoanPosition> fold)
    {
        var p = fold.State;
        var hasNextOccurrence = p.Lifecycle == LoanLifecycle.Active && p.InstallmentsPaid < p.TermMonths;
        return new InstallmentCalendarReadModelRow(
            StreamId: fold.StreamId,
            Sor: "engine",
            FirstInstallmentDate: p.FirstInstallmentDate,
            TermMonths: p.TermMonths,
            InstallmentAmountCents: p.InstallmentAmount.Cents,
            InstallmentsPaid: p.InstallmentsPaid,
            // The forward pointer: occurrence N+1 once N is paid, due-date the anchor rolled forward by the
            // paid count (the same derivation InstallmentCalendarProjection.NextOccurrence makes), gated on
            // a still-live loan with installments remaining — otherwise NULL (exhausted/closed).
            NextInstallmentNumber: hasNextOccurrence ? p.InstallmentsPaid + 1 : null,
            NextDueDate: hasNextOccurrence ? p.FirstInstallmentDate.AddMonths(p.InstallmentsPaid) : null,
            Detail: ReadModelDetailSerializer.Serialize(p),
            LastSequence: fold.SourceSequence,
            LastUpdated: fold.TransactionTime);
    }

    private static readonly JsonStateSerializer<LoanPosition> ReadModelDetailSerializer = new();

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
