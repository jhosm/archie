using Babelstone.Engine;
using Babelstone.EventStore;

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

    /// <summary>The family-prefixed discriminator for the denormalized CQRS read model (D.4, ADR-IC-005).</summary>
    public const string DepositReadModelKind = "term_deposit.deposit_read_model";

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
    /// Builds the family's CQRS read-model runner (D.4, ADR-IC-005): folds the SAME deposit-position
    /// state the live read path computes and maps it to the denormalized <see cref="ReadModelRow"/>
    /// written to <c>read_model.deposits</c>. Declared separately from <see cref="CreateRunners"/>
    /// because the flat read model is a distinct surface from the bitemporal <c>projections</c> store
    /// — distinct store, distinct rebuild discipline (truncate-and-refold, not supersede-all). Async
    /// (v1 default), so it rides the existing drainer/relay unchanged.
    /// </summary>
    public IProjectionRunner CreateReadModelRunner(ReadModelInfra infra) =>
        new ReadModelRunner<DepositPosition>(
            kind: DepositReadModelKind,
            family: FamilyName,
            mode: ProjectionMode.Async,
            handlers: TermDepositFamilyModule.Registry(),
            serializer: infra.EventSerializer,
            seed: () => DepositPosition.Empty,
            map: MapToReadModel,
            store: infra.Store);

    /// <summary>
    /// The pure state→row mapper (no clock, no I/O): projects the folded <see cref="DepositPosition"/>
    /// into the denormalized read-model row. <c>sor = "engine"</c> for every engine-materialised
    /// deposit (ADR-PC-018 §6.2 — set at constitution, never changed). <see cref="ReadModelRow.LastUpdated"/>
    /// is the producing event's transaction_time (event-derived, never the wall clock), so a rebuild
    /// is byte-identical (ADR-PC-010 §P5). The <see cref="ReadModelRow.Detail"/> body is the full
    /// structural state, serialized with the same deterministic JSON codec the bitemporal projection
    /// uses — no PII (ADR-PC-004 §P2). All money is integer cents (ADR-PC-010 §P1).
    /// </summary>
    public static ReadModelRow MapToReadModel(ReadModelFold<DepositPosition> fold)
    {
        var p = fold.State;
        return new ReadModelRow(
            StreamId: fold.StreamId,
            Sor: "engine",
            PrincipalCents: p.Principal.Cents,
            TanBasisPoints: p.TanBasisPoints,
            RateSheetVersionId: p.RateSheetVersionId,
            TermDays: p.TermDays,
            StartDate: p.StartDate,
            MaturityDate: p.MaturityDate,
            InterestVariant: p.InterestVariant,
            Lifecycle: p.Lifecycle.ToString(),
            TotalPayoutCents: p.TotalPayout.Cents,
            Detail: ReadModelDetailSerializer.Serialize(p),
            LastSequence: fold.SourceSequence,
            LastUpdated: fold.TransactionTime);
    }

    // The read-model product key is RateSheetVersionId only — there is deliberately no catalogue
    // product_id column/field. DepositConstituted/DepositPosition do not carry the catalogue product
    // code (e.g. "dpz_pt_12m_juros_venc"): it is resolved into the TAN + rate-sheet version AT
    // constitution and only the version id survives onto the event. A product_id populated from the
    // position would therefore EQUAL the rate_sheet_version_id and a client filtering on the
    // catalogue code would match nothing — a mislabelled column whose name lies about its contents.
    // Carrying the catalogue product code onto DepositConstituted/the position (so the read model can
    // denormalize the real code) is a separable follow-up (bd babelstone-yfr2 deferred note); until
    // then the read surface exposes the version id under its true name only.

    private static readonly Babelstone.Engine.JsonStateSerializer<DepositPosition> ReadModelDetailSerializer = new();

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
