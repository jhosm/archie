using Babelstone.Engine;

namespace Babelstone.Families.TermDeposit;

/// <summary>The kind of dated milestone an entry in the <see cref="MaturityCalendar"/> records.</summary>
public enum MaturityEventKind
{
    /// <summary>The deposit opened — the term's start date (from <c>DepositConstituted</c>).</summary>
    Constituted,

    /// <summary>The SCHEDULED maturity date fixed at constitution (from <c>DepositConstituted</c>).</summary>
    ScheduledMaturity,

    /// <summary>A PERIODIC coupon was paid on this date (from <c>InterestPaid</c>).</summary>
    CouponPaid,

    /// <summary>The deposit actually matured and paid out on this date (from <c>DepositMatured</c>).</summary>
    Matured,

    /// <summary>The deposit auto-renewed; the entry carries the NEW maturity date (from <c>DepositRenewed</c>).</summary>
    Renewed,

    /// <summary>The deposit was broken before maturity on this date (from <c>DepositTerminatedEarly</c>).</summary>
    TerminatedEarly,

    /// <summary>A partial withdrawal occurred on this date (from <c>DepositPartiallyWithdrawn</c>).</summary>
    PartiallyWithdrawn,

    /// <summary>The balance was transferred to heirs on this date (from <c>DepositTransferredToHeirs</c>).</summary>
    TransferredToHeirs,
}

/// <summary>One dated milestone on the deposit's calendar, recorded exactly as the source event
/// carried it.</summary>
/// <remarks>
/// Every date is event-derived (the event's own schedule/effective date) — NEVER the wall clock
/// (BENG001/002/003) — so a cold rebuild reproduces the calendar byte-for-byte (ADR-PC-010 §P5).
/// The fold does no date arithmetic: it does not derive coupon windows or roll dates forward; it
/// records the dates the command-side already fixed.
/// </remarks>
/// <param name="Kind">Which milestone this entry records.</param>
/// <param name="Date">The milestone's date, carried by the source event.</param>
public sealed record MaturityCalendarEntry(MaturityEventKind Kind, DateOnly Date);

/// <summary>
/// The maturity-calendar projection (F.6, babelstone-3kjl): the per-stream timeline of a term
/// deposit's dated milestones (start, scheduled maturity, coupons, actual maturity, renewal, early
/// termination, withdrawals, succession), folded from the family's date-bearing events. Kind
/// <c>term_deposit.maturity_calendar</c>; <see cref="ProjectionMode.Async"/> like every v1
/// projection. Modelled as a state record holding a COLLECTION so it fits the existing store shape
/// unchanged — one current-belief row per <c>(stream_id, projection_kind)</c> carrying the whole
/// calendar (no per-row schema change; ADR-PC-002 §P1).
/// </summary>
/// <remarks>
/// The fold is pure and append-only: each date-bearing event APPENDS milestone entries in fold
/// order. <c>DepositConstituted</c> appends two (the start and the scheduled maturity) so the
/// calendar shows the planned maturity alongside whatever actually happened. The append-only shape
/// is replay-safe because the runner's <c>source_sequence</c> guard skips a re-delivered event
/// (ProjectionRunner remarks); no milestone is recorded twice. No money lives here — this
/// projection is purely temporal.
/// </remarks>
/// <param name="Entries">The dated milestones in fold order (append-only).</param>
public sealed record MaturityCalendar(IReadOnlyList<MaturityCalendarEntry> Entries)
{
    /// <summary>The seed state a fold starts from (before any event).</summary>
    public static MaturityCalendar Empty { get; } = new([]);
}

// Pure folds (state, event) → state for the maturity-calendar projection. No clock, no I/O,
// no randomness (BENG001/002/003); each body is a single `state with { … }` that APPENDS the
// recorded date(s). The fold records the dates the events already carry — it never derives or
// rolls a date itself.

public sealed class MaturityCalendarConstitutedHandler : IEventHandler<MaturityCalendar, DepositConstituted>
{
    public HandlerResult<MaturityCalendar> Apply(MaturityCalendar state, DepositConstituted @event)
        => HandlerResult<MaturityCalendar>.From(state with
        {
            // Both the start and the SCHEDULED maturity are fixed at constitution; record both so
            // the calendar carries the plan as well as the realised milestones folded later.
            Entries =
            [
                .. state.Entries,
                new MaturityCalendarEntry(MaturityEventKind.Constituted, @event.StartDate),
                new MaturityCalendarEntry(MaturityEventKind.ScheduledMaturity, @event.MaturityDate),
            ],
        });
}

public sealed class MaturityCalendarInterestPaidHandler : IEventHandler<MaturityCalendar, InterestPaid>
{
    public HandlerResult<MaturityCalendar> Apply(MaturityCalendar state, InterestPaid @event)
        => HandlerResult<MaturityCalendar>.From(state with
        {
            Entries = [.. state.Entries, new MaturityCalendarEntry(MaturityEventKind.CouponPaid, @event.PaidOn)],
        });
}

public sealed class MaturityCalendarMaturedHandler : IEventHandler<MaturityCalendar, DepositMatured>
{
    public HandlerResult<MaturityCalendar> Apply(MaturityCalendar state, DepositMatured @event)
        => HandlerResult<MaturityCalendar>.From(state with
        {
            Entries = [.. state.Entries, new MaturityCalendarEntry(MaturityEventKind.Matured, @event.MaturedOn)],
        });
}

public sealed class MaturityCalendarRenewedHandler : IEventHandler<MaturityCalendar, DepositRenewed>
{
    public HandlerResult<MaturityCalendar> Apply(MaturityCalendar state, DepositRenewed @event)
        => HandlerResult<MaturityCalendar>.From(state with
        {
            // The renewal both closes this term and fixes the NEW maturity date; record the new one.
            Entries = [.. state.Entries, new MaturityCalendarEntry(MaturityEventKind.Renewed, @event.NewMaturityDate)],
        });
}

public sealed class MaturityCalendarTerminatedEarlyHandler : IEventHandler<MaturityCalendar, DepositTerminatedEarly>
{
    public HandlerResult<MaturityCalendar> Apply(MaturityCalendar state, DepositTerminatedEarly @event)
        => HandlerResult<MaturityCalendar>.From(state with
        {
            Entries = [.. state.Entries, new MaturityCalendarEntry(MaturityEventKind.TerminatedEarly, @event.TerminatedOn)],
        });
}

public sealed class MaturityCalendarPartiallyWithdrawnHandler : IEventHandler<MaturityCalendar, DepositPartiallyWithdrawn>
{
    public HandlerResult<MaturityCalendar> Apply(MaturityCalendar state, DepositPartiallyWithdrawn @event)
        => HandlerResult<MaturityCalendar>.From(state with
        {
            Entries = [.. state.Entries, new MaturityCalendarEntry(MaturityEventKind.PartiallyWithdrawn, @event.WithdrawnOn)],
        });
}

public sealed class MaturityCalendarTransferredToHeirsHandler : IEventHandler<MaturityCalendar, DepositTransferredToHeirs>
{
    public HandlerResult<MaturityCalendar> Apply(MaturityCalendar state, DepositTransferredToHeirs @event)
        => HandlerResult<MaturityCalendar>.From(state with
        {
            Entries = [.. state.Entries, new MaturityCalendarEntry(MaturityEventKind.TransferredToHeirs, @event.TransferDate)],
        });
}
