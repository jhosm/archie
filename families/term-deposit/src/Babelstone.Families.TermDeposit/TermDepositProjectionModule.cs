using Babelstone.Engine;

namespace Babelstone.Families.TermDeposit;

/// <summary>
/// The term-deposit family's projection declarations (two-modes §5.4: declared in the family,
/// not hardcoded in the engine). D.2 declared ONE projection — the deposit position; F.6
/// (babelstone-3kjl) completes the set of four by adding the accrual schedule, maturity calendar,
/// and withholding ledger as further runners here. Every v1 projection is
/// <see cref="ProjectionMode.Async"/>.
/// </summary>
/// <remarks>
/// <para>
/// The deposit-position runner reuses the family's own folds (the same
/// <see cref="TermDepositFamilyModule.Registry"/> the durable runtime uses), so the position
/// materialised into the bitemporal table is the SAME fold the live read path computes — that
/// equivalence is what D.5's reconciliation drill asserts. The three F.6 projections each have
/// their OWN state type, seed, and a dedicated <see cref="HandlerRegistry"/> of folds that ignore
/// every event type they do not record (the runner skips an unhandled type, leaving the belief
/// unchanged — see <see cref="ProjectionRunner{TState}"/>). Each is its own current-belief row per
/// <c>(stream_id, projection_kind)</c>: the store shape is unchanged (ADR-PC-002 §P1) — a
/// schedule/calendar/ledger is modelled as a state record holding a collection, not as new rows or
/// columns. The engine spine never names this family; the host composes infra + this declaration
/// (ADR-PC-021 §D4).
/// </para>
/// <para>
/// All four folds are pure, accumulating/append-only, and record the COMPUTED facts the events
/// carry (never recomputing accrual or re-deriving withholding from a rate; financial-math §5.4),
/// so a cold rebuild reproduces each belief byte-for-byte (ADR-PC-010 §P5).
/// </para>
/// </remarks>
public sealed class TermDepositProjectionModule : IProjectionModule
{
    /// <summary>The family-prefixed discriminator for the deposit-position projection (migration 0010).</summary>
    public const string DepositPositionKind = "term_deposit.deposit_position";

    /// <summary>The family-prefixed discriminator for the accrual-schedule projection (F.6).</summary>
    public const string AccrualScheduleKind = "term_deposit.accrual_schedule";

    /// <summary>The family-prefixed discriminator for the maturity-calendar projection (F.6).</summary>
    public const string MaturityCalendarKind = "term_deposit.maturity_calendar";

    /// <summary>The family-prefixed discriminator for the withholding-ledger projection (F.6).</summary>
    public const string WithholdingLedgerKind = "term_deposit.withholding_ledger";

    public string FamilyName => "term_deposit";

    public IReadOnlyList<IProjectionRunner> CreateRunners(ProjectionInfra infra)
    {
        var depositPosition = new ProjectionRunner<DepositPosition>(
            kind: DepositPositionKind,
            family: FamilyName,
            mode: ProjectionMode.Async,
            handlers: TermDepositFamilyModule.Registry(),
            serializer: infra.EventSerializer,
            seed: () => DepositPosition.Empty,
            store: new ProjectionStore<DepositPosition>(infra.Storage, new JsonStateSerializer<DepositPosition>()));

        var accrualSchedule = new ProjectionRunner<AccrualSchedule>(
            kind: AccrualScheduleKind,
            family: FamilyName,
            mode: ProjectionMode.Async,
            handlers: AccrualScheduleRegistry(),
            serializer: infra.EventSerializer,
            seed: () => AccrualSchedule.Empty,
            store: new ProjectionStore<AccrualSchedule>(infra.Storage, new JsonStateSerializer<AccrualSchedule>()));

        var maturityCalendar = new ProjectionRunner<MaturityCalendar>(
            kind: MaturityCalendarKind,
            family: FamilyName,
            mode: ProjectionMode.Async,
            handlers: MaturityCalendarRegistry(),
            serializer: infra.EventSerializer,
            seed: () => MaturityCalendar.Empty,
            store: new ProjectionStore<MaturityCalendar>(infra.Storage, new JsonStateSerializer<MaturityCalendar>()));

        var withholdingLedger = new ProjectionRunner<WithholdingLedger>(
            kind: WithholdingLedgerKind,
            family: FamilyName,
            mode: ProjectionMode.Async,
            handlers: WithholdingLedgerRegistry(),
            serializer: infra.EventSerializer,
            seed: () => WithholdingLedger.Empty,
            store: new ProjectionStore<WithholdingLedger>(infra.Storage, new JsonStateSerializer<WithholdingLedger>()));

        return [depositPosition, accrualSchedule, maturityCalendar, withholdingLedger];
    }

    /// <summary>
    /// The accrual-schedule fold registry: only the accrual-bearing event types (the runner skips
    /// every other family event, leaving the schedule unchanged). Exposed so tests fold the same
    /// bindings the runner uses.
    /// </summary>
    public static HandlerRegistry AccrualScheduleRegistry() => new(
    [
        new("term_deposit.InterestAccrued", typeof(InterestAccrued),
            new DispatchableHandler<AccrualSchedule, InterestAccrued>(new AccrualScheduleInterestAccruedHandler())),
        new("term_deposit.InterestPaid", typeof(InterestPaid),
            new DispatchableHandler<AccrualSchedule, InterestPaid>(new AccrualScheduleInterestPaidHandler())),
    ]);

    /// <summary>
    /// The maturity-calendar fold registry: only the date-bearing event types (the runner skips
    /// every other family event). Exposed so tests fold the same bindings the runner uses.
    /// </summary>
    public static HandlerRegistry MaturityCalendarRegistry() => new(
    [
        new("term_deposit.DepositConstituted", typeof(DepositConstituted),
            new DispatchableHandler<MaturityCalendar, DepositConstituted>(new MaturityCalendarConstitutedHandler())),
        new("term_deposit.InterestPaid", typeof(InterestPaid),
            new DispatchableHandler<MaturityCalendar, InterestPaid>(new MaturityCalendarInterestPaidHandler())),
        new("term_deposit.DepositMatured", typeof(DepositMatured),
            new DispatchableHandler<MaturityCalendar, DepositMatured>(new MaturityCalendarMaturedHandler())),
        new("term_deposit.DepositRenewed", typeof(DepositRenewed),
            new DispatchableHandler<MaturityCalendar, DepositRenewed>(new MaturityCalendarRenewedHandler())),
        new("term_deposit.DepositTerminatedEarly", typeof(DepositTerminatedEarly),
            new DispatchableHandler<MaturityCalendar, DepositTerminatedEarly>(new MaturityCalendarTerminatedEarlyHandler())),
        new("term_deposit.DepositPartiallyWithdrawn", typeof(DepositPartiallyWithdrawn),
            new DispatchableHandler<MaturityCalendar, DepositPartiallyWithdrawn>(new MaturityCalendarPartiallyWithdrawnHandler())),
        new("term_deposit.DepositTransferredToHeirs", typeof(DepositTransferredToHeirs),
            new DispatchableHandler<MaturityCalendar, DepositTransferredToHeirs>(new MaturityCalendarTransferredToHeirsHandler())),
    ]);

    /// <summary>
    /// The withholding-ledger fold registry: only the withholding-bearing event types (the runner
    /// skips every other family event). Exposed so tests fold the same bindings the runner uses.
    /// </summary>
    public static HandlerRegistry WithholdingLedgerRegistry() => new(
    [
        new("term_deposit.WithholdingApplied", typeof(WithholdingApplied),
            new DispatchableHandler<WithholdingLedger, WithholdingApplied>(new WithholdingLedgerWithholdingAppliedHandler())),
        new("term_deposit.InterestPaid", typeof(InterestPaid),
            new DispatchableHandler<WithholdingLedger, InterestPaid>(new WithholdingLedgerInterestPaidHandler())),
    ]);
}
