using Babelstone.Engine;
using Babelstone.FinancialTypes;

namespace Babelstone.Families.TermDeposit;

/// <summary>One accrual flow as it was RECORDED on the stream — a single
/// <c>InterestAccrued</c> (the AT_MATURITY single flow, or one accrual leg of a
/// PERIODIC/ADVANCE schedule) or the gross-interest leg of an <c>InterestPaid</c> coupon.</summary>
/// <remarks>
/// The schedule is descriptive, never prescriptive: it records the gross interest the
/// command-side financial-math kernel already computed and stamped on the event (E.3 /
/// ADR-PC-010 §P1). The fold NEVER recomputes accrual — no <c>Accrual.SimpleInterest</c>,
/// no day-count, no rate-scaling lives here (BENG001/002/003). <paramref name="AsOf"/> is the
/// event's own as-of/paid date, so the slice is event-derived and a cold rebuild reproduces it
/// byte-for-byte (ADR-PC-010 §P5).
/// </remarks>
/// <param name="AsOf">The accrual flow's as-of date, carried by the source event (never a clock).</param>
/// <param name="GrossInterest">The gross interest for this flow, exactly as recorded (cents, <see cref="Money"/>).</param>
/// <param name="Source">Which event produced the entry — <c>"accrued"</c> for <c>InterestAccrued</c>,
/// <c>"coupon"</c> for an <c>InterestPaid</c> leg. Lets a reader separate accruals from paid coupons
/// without re-deriving anything.</param>
public sealed record AccrualEntry(DateOnly AsOf, Money GrossInterest, string Source);

/// <summary>
/// The accrual-schedule projection (F.6, babelstone-3kjl): the per-stream timeline of interest
/// accrual flows for a term deposit, folded from the family's accrual-bearing events. Kind
/// <c>term_deposit.accrual_schedule</c>; <see cref="ProjectionMode.Async"/> like every v1
/// projection. Modelled as a state record holding a COLLECTION so it fits the existing store
/// shape unchanged — one current-belief row per <c>(stream_id, projection_kind)</c> carrying the
/// whole schedule (no per-row schema change; ADR-PC-002 §P1).
/// </summary>
/// <remarks>
/// The fold is pure and accumulating: each accrual-bearing event APPENDS an
/// <see cref="AccrualEntry"/> (recorded as stamped) and adds to <see cref="TotalGrossAccrued"/>.
/// It folds the SAME <c>GrossInterest</c> the <see cref="DepositPosition"/> fold sums, so the
/// schedule's total reconciles with the position's <c>AccruedGrossInterest</c> by construction
/// (D.5's reconciliation drill). The accumulating shape is replay-safe because the runner's
/// <c>source_sequence</c> guard skips a re-delivered event (ProjectionRunner remarks); no entry is
/// ever appended twice. All money is cents (<see cref="Money"/>); no <c>decimal</c> state
/// (ADR-PC-010 §P1, BMNY002).
/// </remarks>
/// <param name="Entries">The accrual flows in fold order (append-only) — the recorded timeline.</param>
/// <param name="TotalGrossAccrued">Running sum of every entry's gross interest, conserved to the cent.</param>
public sealed record AccrualSchedule(
    IReadOnlyList<AccrualEntry> Entries,
    Money TotalGrossAccrued)
{
    /// <summary>The seed state a fold starts from (before any accrual event).</summary>
    public static AccrualSchedule Empty { get; } = new([], Money.Zero);
}

// Pure folds (state, event) → state for the accrual-schedule projection. No clock, no I/O,
// no randomness (BENG001/002/003); each body is a single `state with { … }` that APPENDS the
// recorded flow and accumulates the running total via Money's checked + operator. The fold
// records what the event already carries — it never recomputes accrual (no rate-scaling,
// no day-count), honouring the financial-math §5.4 flow-by-flow discipline.

public sealed class AccrualScheduleInterestAccruedHandler : IEventHandler<AccrualSchedule, InterestAccrued>
{
    public HandlerResult<AccrualSchedule> Apply(AccrualSchedule state, InterestAccrued @event)
        => HandlerResult<AccrualSchedule>.From(state with
        {
            Entries = [.. state.Entries, new AccrualEntry(@event.AsOf, @event.GrossInterest, "accrued")],
            TotalGrossAccrued = state.TotalGrossAccrued + @event.GrossInterest,
        });
}

public sealed class AccrualScheduleInterestPaidHandler : IEventHandler<AccrualSchedule, InterestPaid>
{
    public HandlerResult<AccrualSchedule> Apply(AccrualSchedule state, InterestPaid @event)
        => HandlerResult<AccrualSchedule>.From(state with
        {
            // A PERIODIC/ADVANCE coupon is also an accrual flow; record its gross leg as stamped
            // (the event already carries gross = tax + net, computed command-side).
            Entries = [.. state.Entries, new AccrualEntry(@event.PaidOn, @event.GrossInterest, "coupon")],
            TotalGrossAccrued = state.TotalGrossAccrued + @event.GrossInterest,
        });
}
