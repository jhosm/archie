using Babelstone.Engine;
using Babelstone.EventStore;

namespace Babelstone.Families.CreditoPessoal;

/// <summary>
/// The credito_pessoal family's projection declarations (two-modes §5.4: declared in the family, not
/// hardcoded in the engine). Two projections: the loan POSITION (folded over the family's own handlers,
/// the live amortizing state) and the AMORTIZATION SCHEDULE (as actually paid).
/// Every v1 projection is <see cref="ProjectionMode.Async"/>. The engine spine never names this family;
/// the host composes infra + this declaration (ADR-PC-021 §D4).
/// </summary>
/// <remarks>
/// The loan-position runner reuses the family's own folds (the same
/// <see cref="CreditoPessoalFamilyModule.Registry"/> the durable runtime uses), so the position
/// materialised into the bitemporal table is the SAME fold the live read path computes. The
/// amortization-schedule projection has its OWN state type, seed, and a dedicated
/// <see cref="HandlerRegistry"/> of folds that ignore every event type they do not record (the runner
/// skips an unhandled type, leaving the belief unchanged). Each is its own current-belief row per
/// <c>(stream_id, projection_kind)</c>. Both folds are pure, accumulating/append-only, and record the
/// COMPUTED facts the events carry (never recomputing the amortization split from a rate), so a cold
/// rebuild reproduces each belief byte-for-byte (ADR-PC-010 §P5).
/// </remarks>
public sealed class CreditoPessoalProjectionModule : IProjectionModule
{
    /// <summary>The family-prefixed discriminator for the loan-position projection.</summary>
    public const string LoanPositionKind = "credito_pessoal.loan_position";

    /// <summary>The family-prefixed discriminator for the amortization-schedule projection.</summary>
    public const string AmortizationScheduleKind = "credito_pessoal.amortization_schedule";

    public string FamilyName => "credito_pessoal";

    public IReadOnlyList<IProjectionRunner> CreateRunners(ProjectionInfra infra)
    {
        var loanPosition = new ProjectionRunner<LoanPosition>(
            kind: LoanPositionKind,
            family: FamilyName,
            mode: ProjectionMode.Async,
            handlers: CreditoPessoalFamilyModule.Registry(),
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

        return [loanPosition, amortizationSchedule];
    }

    /// <summary>
    /// The amortization-schedule fold registry: only the balance-changing event types (the runner skips
    /// every other family event, leaving the schedule unchanged). Exposed so tests fold the same bindings
    /// the runner uses.
    /// </summary>
    public static HandlerRegistry AmortizationScheduleRegistry() => new(
    [
        new("credito_pessoal.LoanInstallmentPaid", typeof(LoanInstallmentPaid),
            new DispatchableHandler<AmortizationScheduleProjection, LoanInstallmentPaid>(
                new AmortizationScheduleInstallmentHandler())),
        new("credito_pessoal.LoanRepaidEarly", typeof(LoanRepaidEarly),
            new DispatchableHandler<AmortizationScheduleProjection, LoanRepaidEarly>(
                new AmortizationScheduleEarlyRepaymentHandler())),
    ]);
}
