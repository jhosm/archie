using Babelstone.Engine;
using Babelstone.FinancialTypes;

namespace Babelstone.Families.PersonalLoan;

/// <summary>One installment as it was RECORDED on the stream — a single <c>LoanInstallmentPaid</c>
/// (one row of the amortization schedule as actually paid) or an early-repayment leg.</summary>
/// <remarks>
/// The schedule projection is descriptive, never prescriptive: it records the interest/capital split
/// the command-side amortization kernel already computed and stamped on the event (ADR-PC-010 §P1).
/// The fold NEVER recomputes the split — no <c>Amortization.Schedule</c>, no rate-scaling lives here
/// (BENG001/002/003). <paramref name="PaidOn"/> is the event's own date, so the slice is event-derived
/// and a cold rebuild reproduces it byte-for-byte (ADR-PC-010 §P5).
/// </remarks>
/// <param name="InstallmentNumber">The 1-based installment index, or 0 for an early-repayment leg.</param>
/// <param name="PaidOn">The installment's paid date, carried by the source event (never a clock).</param>
/// <param name="Interest">The interest leg for this entry, exactly as recorded (cents, <see cref="Money"/>).</param>
/// <param name="Capital">The capital-amortized leg for this entry, exactly as recorded (cents).</param>
/// <param name="OutstandingBalance">The capital still owed after this entry, exactly as recorded (cents).</param>
/// <param name="Source">Which event produced the entry — <c>"installment"</c> for
/// <c>LoanInstallmentPaid</c>, <c>"early_repayment"</c> for <c>LoanRepaidEarly</c>.</param>
public sealed record AmortizationEntry(
    int InstallmentNumber,
    DateOnly PaidOn,
    Money Interest,
    Money Capital,
    Money OutstandingBalance,
    string Source);

/// <summary>
/// The amortization-schedule projection (the schedule as actually paid): the
/// per-stream timeline of installment + early-repayment flows for a loan, folded from the family's
/// balance-changing events. Kind <c>personal_loan.amortization_schedule</c>;
/// <see cref="ProjectionMode.Async"/> like every v1 projection. Modelled as a state record holding a
/// COLLECTION so it fits the existing store shape unchanged — one current-belief row per
/// <c>(stream_id, projection_kind)</c> carrying the whole schedule (ADR-PC-002 §P1).
/// </summary>
/// <remarks>
/// The fold is pure and accumulating: each event APPENDS an <see cref="AmortizationEntry"/> (recorded as
/// stamped) and adds to the running totals. It folds the SAME interest/capital the
/// <see cref="LoanPosition"/> fold sums, so the schedule's totals reconcile with the position's
/// <c>TotalInterestPaid</c>/<c>TotalCapitalRepaid</c> by construction. The accumulating shape is
/// replay-safe because the runner's <c>source_sequence</c> guard skips a re-delivered event; no entry is
/// ever appended twice. All money is cents (ADR-PC-010 §P1, BMNY002).
/// </remarks>
/// <param name="Entries">The amortization flows in fold order (append-only) — the recorded timeline.</param>
/// <param name="TotalInterest">Running sum of every entry's interest, conserved to the cent.</param>
/// <param name="TotalCapital">Running sum of every entry's capital repaid, conserved to the cent.</param>
public sealed record AmortizationScheduleProjection(
    IReadOnlyList<AmortizationEntry> Entries,
    Money TotalInterest,
    Money TotalCapital)
{
    /// <summary>The seed state a fold starts from (before any installment).</summary>
    public static AmortizationScheduleProjection Empty { get; } = new([], Money.Zero, Money.Zero);
}

// Pure folds (state, event) → state for the amortization-schedule projection. No clock, no I/O, no
// randomness (BENG001/002/003); each body APPENDS the recorded flow and accumulates the running totals
// via Money's checked + operator. The fold records what the event already carries — it never recomputes
// the amortization split (no Amortization.Schedule, no rate-scaling).

public sealed class AmortizationScheduleInstallmentHandler
    : IEventHandler<AmortizationScheduleProjection, LoanInstallmentPaid>
{
    public HandlerResult<AmortizationScheduleProjection> Apply(
        AmortizationScheduleProjection state, LoanInstallmentPaid @event)
        => HandlerResult<AmortizationScheduleProjection>.From(state with
        {
            Entries = [.. state.Entries, new AmortizationEntry(
                @event.InstallmentNumber, @event.PaidOn, @event.Interest, @event.Capital,
                @event.OutstandingBalance, "installment")],
            TotalInterest = state.TotalInterest + @event.Interest,
            TotalCapital = state.TotalCapital + @event.Capital,
        });
}

public sealed class AmortizationScheduleEarlyRepaymentHandler
    : IEventHandler<AmortizationScheduleProjection, LoanRepaidEarly>
{
    public HandlerResult<AmortizationScheduleProjection> Apply(
        AmortizationScheduleProjection state, LoanRepaidEarly @event)
        // An early repayment carries no scheduled-interest leg (it returns capital + a separate
        // commission); record it as a zero-interest capital flow so the schedule timeline is complete.
        => HandlerResult<AmortizationScheduleProjection>.From(state with
        {
            Entries = [.. state.Entries, new AmortizationEntry(
                0, @event.RepaidOn, Money.Zero, @event.CapitalRepaid,
                @event.OutstandingBalanceAfter, "early_repayment")],
            TotalCapital = state.TotalCapital + @event.CapitalRepaid,
        });
}
