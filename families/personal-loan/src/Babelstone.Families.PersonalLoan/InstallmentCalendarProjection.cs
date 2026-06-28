using System.Text.Json.Serialization;
using Babelstone.Engine;
using Babelstone.FinancialTypes;

namespace Babelstone.Families.PersonalLoan;

/// <summary>One forward, not-yet-paid installment the loan still owes — the NEXT occurrence the
/// installment-calendar projection surfaces.</summary>
/// <remarks>
/// Derived, not stored: its <paramref name="DueDate"/> rolls the schedule anchor
/// (<c>FirstInstallmentDate</c>) forward by the number of installments already paid — deterministic
/// date arithmetic on an event-carried date, NEVER a wall-clock read (BENG001/002/003). The
/// <paramref name="Amount"/> is the level (constant) installment the disbursement fixed.
/// </remarks>
/// <param name="InstallmentNumber">The 1-based index of the next unpaid installment (N+1 once N is paid).</param>
/// <param name="DueDate">The next installment's due date, event-derived (anchor + paid count months).</param>
/// <param name="Amount">The level installment amount (cents, <see cref="Money"/>), fixed at disbursement.</param>
public sealed record InstallmentOccurrence(
    int InstallmentNumber,
    DateOnly DueDate,
    Money Amount);

/// <summary>
/// The installment-calendar projection (the loan's FORWARD-looking schedule): the next still-unpaid
/// installment a loan owes, folded from the loan's disbursement and its paid installments. Kind
/// <c>personal_loan.installment_calendar</c>; <see cref="ProjectionMode.Async"/> like every v1
/// projection. The closed-end-asset analogue of the term deposit's <c>MaturityCalendar</c> — but
/// where the maturity calendar records the dated milestones that ALREADY happened (descriptive, append-only),
/// this calendar surfaces the single next occurrence still AHEAD (a forward pointer, recomputed each fold).
/// </summary>
/// <remarks>
/// The fold is pure: no clock, no I/O, no randomness (BENG001/002/003). It folds exactly two event types —
/// <see cref="LoanDisbursed"/> fixes the schedule (anchor + term + level amount) and each
/// <see cref="LoanInstallmentPaid"/> advances the paid count — and the runner skips every other family
/// event, leaving the belief unchanged. The next occurrence is DERIVED from that state on read
/// (<see cref="NextOccurrence"/>): occurrence N+1 once N is paid, with its due date the anchor rolled
/// forward by the paid count. A fully-paid loan (every scheduled installment collected) surfaces NO further
/// occurrence — the calendar is exhausted. The fold advances the paid count by the highest installment
/// number it has seen (a MAX, not a running +1), so a re-delivered installment leaves the forward pointer
/// unchanged: idempotent in the fold itself, on top of the runner's <c>source_sequence</c> dedup, so a cold
/// rebuild reproduces the belief byte-for-byte (ADR-PC-010 §P5). All money is cents (ADR-PC-010 §P1,
/// BMNY002); no PII (ADR-PC-004 §P2) — the schedule is purely structural.
/// </remarks>
/// <param name="FirstInstallmentDate">The first installment's due date (the schedule anchor), folded
/// from disbursement. Occurrence k's due date is this rolled forward by <c>k − 1</c> months.</param>
/// <param name="TermMonths">The number of scheduled monthly installments <c>n</c>, folded from disbursement.</param>
/// <param name="InstallmentAmount">The level (constant) installment, folded from disbursement.</param>
/// <param name="InstallmentsPaid">How many scheduled installments have been paid (the highest installment
/// number folded); the next unpaid occurrence is number <c>InstallmentsPaid + 1</c>.</param>
public sealed record InstallmentCalendarProjection(
    DateOnly FirstInstallmentDate,
    int TermMonths,
    Money InstallmentAmount,
    int InstallmentsPaid)
{
    /// <summary>The seed state a fold starts from (before <c>LoanDisbursed</c> — no schedule yet).</summary>
    public static InstallmentCalendarProjection Empty { get; } =
        new(default, 0, Money.Zero, 0);

    /// <summary>
    /// Is there a next unpaid occurrence? False before disbursement (no schedule, <c>TermMonths == 0</c>)
    /// AND once every scheduled installment is paid (<c>InstallmentsPaid == TermMonths</c>) — a fully-paid
    /// loan surfaces none. Derived, so <c>[JsonIgnore]</c>: the stored belief carries only the folded
    /// fields, never this computed convenience (keeps the rebuilt payload byte-minimal and unambiguous).
    /// </summary>
    [JsonIgnore]
    public bool HasNextOccurrence => TermMonths > 0 && InstallmentsPaid < TermMonths;

    /// <summary>
    /// The next-unpaid occurrence (number <c>InstallmentsPaid + 1</c>, 1-based) — or <c>null</c> when the
    /// loan is fully paid or not yet disbursed. Its due date is <see cref="FirstInstallmentDate"/> rolled
    /// forward by the paid count (deterministic date arithmetic on event-carried dates, never a clock).
    /// Derived, so <c>[JsonIgnore]</c> — recomputed on read from the folded state above.
    /// </summary>
    [JsonIgnore]
    public InstallmentOccurrence? NextOccurrence =>
        HasNextOccurrence
            ? new InstallmentOccurrence(
                InstallmentsPaid + 1,
                FirstInstallmentDate.AddMonths(InstallmentsPaid),
                InstallmentAmount)
            : null;
}

// Pure folds (state, event) → state for the installment-calendar projection. No clock, no I/O, no
// randomness (BENG001/002/003); each body is a single `state with { … }` that records the schedule
// fact the event carries. The fold never derives the next due date itself — that is computed on READ
// (InstallmentCalendarProjection.NextOccurrence) from the folded anchor + paid count.

public sealed class InstallmentCalendarDisbursedHandler
    : IEventHandler<InstallmentCalendarProjection, LoanDisbursed>
{
    public HandlerResult<InstallmentCalendarProjection> Apply(
        InstallmentCalendarProjection state, LoanDisbursed @event)
        // Disbursement FIXES the schedule: the anchor (first installment due date), the term (how many
        // installments), and the level amount. The forward pointer starts at occurrence 1 (no paid count yet).
        => HandlerResult<InstallmentCalendarProjection>.From(state with
        {
            FirstInstallmentDate = @event.FirstInstallmentDate,
            TermMonths = @event.TermMonths,
            InstallmentAmount = @event.InstallmentAmount,
        });
}

public sealed class InstallmentCalendarInstallmentPaidHandler
    : IEventHandler<InstallmentCalendarProjection, LoanInstallmentPaid>
{
    public HandlerResult<InstallmentCalendarProjection> Apply(
        InstallmentCalendarProjection state, LoanInstallmentPaid @event)
        // Paying installment N advances the paid count to N, so the next unpaid occurrence becomes N+1.
        // MAX (not a running +1) so a re-delivered installment leaves the forward pointer unchanged — the
        // fold is idempotent in itself, beneath the runner's source_sequence dedup.
        => HandlerResult<InstallmentCalendarProjection>.From(state with
        {
            InstallmentsPaid = Math.Max(state.InstallmentsPaid, @event.InstallmentNumber),
        });
}
