using Babelstone.Engine;
using Babelstone.EventStore;
using Babelstone.Families.TermDeposit;

namespace Babelstone.Notification;

/// <summary>
/// The notification service's READ window onto the engine's term-deposit projections
/// (ADR-IC-005 CQRS read surface). In plain terms: the maturity scheduler this service will host
/// needs to know, per deposit, when it matures, what interest it has accrued, and what tax was
/// withheld — and all three are already pre-computed by the engine and parked in PostgreSQL read
/// models. This reader is the thin, READ-ONLY adapter that pulls the currently-believed row of each
/// of those three projections and hands back the typed family state. There is NO scheduling and NO
/// emission here (those are the downstream children, bd babelstone-60n8.2 / .3) — this skeleton just
/// proves the host can reach the read surface.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why straight to PostgreSQL.</b> ADR-IC-005 makes PostgreSQL the SOLE read-model storage
/// technology, so a downstream consumer reads a projection by querying that store directly — the
/// same byte-oriented boundary (<see cref="IProjectionStorage"/> / <c>PostgresProjectionStore</c>)
/// the engine writes through. The notification host owns a RUNTIME-role connection string resolved
/// at its composition root (the ADR-PC-004 Amendment A1 credential boundary) with SELECT-only grant
/// on the <c>projections</c> table; the credential never rides a message or the durable bus.
/// </para>
/// <para>
/// <b>Why these three kinds.</b> The maturity scheduler is driven by the maturity calendar; the
/// disclosure content it will later emit is sourced from the accrual schedule and the withholding
/// ledger. All three projections exist and are registered today in
/// <see cref="TermDepositProjectionModule"/>; this reader keys each read on that module's own
/// family-prefixed discriminator constants so the kind strings cannot drift from the writer side.
/// </para>
/// <para>
/// <b>Typed reads.</b> Each read goes through <see cref="BitemporalProjectionQuery{TState}"/> — the
/// engine's own typed query helper (ADR-PC-002 §P3) — over a <see cref="JsonStateSerializer{TState}"/>
/// that matches the deterministic JSON codec the projection was written with. The notification
/// service reads only the CURRENT belief (<c>superseded_at IS NULL</c>): a notification reflects the
/// world as currently known, not a historical or counterfactual belief slice. A stream with no such
/// projection yet returns <see langword="null"/> (no row materialised).
/// </para>
/// </remarks>
public sealed class TermDepositProjectionReader
{
    private readonly BitemporalProjectionQuery<MaturityCalendar> _maturityCalendar;
    private readonly BitemporalProjectionQuery<AccrualSchedule> _accrualSchedule;
    private readonly BitemporalProjectionQuery<WithholdingLedger> _withholdingLedger;

    /// <summary>
    /// Composes the three typed query helpers over the shared byte-oriented projection store. One
    /// <see cref="IProjectionStorage"/> backs all three kinds — they are distinguished by the
    /// <c>(stream_id, projection_kind)</c> pair, not by separate stores (ADR-PC-002 §P1).
    /// </summary>
    /// <param name="storage">The byte-oriented projection boundary onto the engine's read-model
    /// store (PostgreSQL, ADR-IC-005). Resolved at the host composition root over the runtime-role
    /// connection.</param>
    public TermDepositProjectionReader(IProjectionStorage storage)
    {
        ArgumentNullException.ThrowIfNull(storage);
        _maturityCalendar = new BitemporalProjectionQuery<MaturityCalendar>(
            storage, new JsonStateSerializer<MaturityCalendar>());
        _accrualSchedule = new BitemporalProjectionQuery<AccrualSchedule>(
            storage, new JsonStateSerializer<AccrualSchedule>());
        _withholdingLedger = new BitemporalProjectionQuery<WithholdingLedger>(
            storage, new JsonStateSerializer<WithholdingLedger>());
    }

    /// <summary>
    /// The currently-believed <see cref="MaturityCalendar"/> for <paramref name="streamId"/>, or
    /// <see langword="null"/> if no calendar projection has materialised for that deposit yet
    /// (<c>term_deposit.maturity_calendar</c>).
    /// </summary>
    public async Task<MaturityCalendar?> ReadMaturityCalendarAsync(Guid streamId, CancellationToken ct = default)
    {
        var belief = await _maturityCalendar.CurrentBeliefAsync(
            streamId, TermDepositProjectionModule.MaturityCalendarKind, ct);
        return belief?.State;
    }

    /// <summary>
    /// The currently-believed <see cref="AccrualSchedule"/> for <paramref name="streamId"/>, or
    /// <see langword="null"/> if no accrual-schedule projection has materialised for that deposit
    /// yet (<c>term_deposit.accrual_schedule</c>).
    /// </summary>
    public async Task<AccrualSchedule?> ReadAccrualScheduleAsync(Guid streamId, CancellationToken ct = default)
    {
        var belief = await _accrualSchedule.CurrentBeliefAsync(
            streamId, TermDepositProjectionModule.AccrualScheduleKind, ct);
        return belief?.State;
    }

    /// <summary>
    /// The currently-believed <see cref="WithholdingLedger"/> for <paramref name="streamId"/>, or
    /// <see langword="null"/> if no withholding-ledger projection has materialised for that deposit
    /// yet (<c>term_deposit.withholding_ledger</c>).
    /// </summary>
    public async Task<WithholdingLedger?> ReadWithholdingLedgerAsync(Guid streamId, CancellationToken ct = default)
    {
        var belief = await _withholdingLedger.CurrentBeliefAsync(
            streamId, TermDepositProjectionModule.WithholdingLedgerKind, ct);
        return belief?.State;
    }
}
