using Babelstone.Engine;
using Babelstone.FinancialTypes;

namespace Babelstone.Families.PersonalLoan;

// Pure folds (state, event) → state — one per event, mirroring the term-deposit family. No clock,
// no I/O, no randomness (BENG001/002/003); each body is a single `state with { … }`. The money sums
// use Money's own checked + operator (no decimal state, no mid-step rounding). Sums ACCUMULATE
// (state.X + event.Y) and the balance is read off the event (the decider, running the amortization
// kernel command-side, computes the splits and the post-event balance), so the fold stays correct
// under replay and NEVER recomputes accrual or re-derives the amortization split.

public sealed class LoanDisbursedHandler : IEventHandler<LoanPosition, LoanDisbursed>
{
    public HandlerResult<LoanPosition> Apply(LoanPosition state, LoanDisbursed @event)
        => HandlerResult<LoanPosition>.From(state with
        {
            LoanId = @event.LoanId,
            Principal = @event.Principal,
            TanBasisPoints = @event.TanBasisPoints,
            RateSheetVersionId = @event.RateSheetVersionId,
            TermMonths = @event.TermMonths,
            PeriodicRateBasisPoints = @event.PeriodicRateBasisPoints,
            InstallmentAmount = @event.InstallmentAmount,
            StartDate = @event.StartDate,
            FirstInstallmentDate = @event.FirstInstallmentDate,
            Purpose = @event.Purpose,
            ProductCode = @event.ProductCode,
            DisbursementAccountRef = @event.DisbursementAccountRef,
            EarlyRepaymentCommissionBps = @event.EarlyRepaymentCommissionBps,
            // The loan opens owing the full disbursed capital; the balance amortizes from here.
            OutstandingBalance = @event.Principal,
            Lifecycle = LoanLifecycle.Active,
        });
}

public sealed class LoanDisbursementFailedHandler : IEventHandler<LoanPosition, LoanDisbursementFailed>
{
    public HandlerResult<LoanPosition> Apply(LoanPosition state, LoanDisbursementFailed @event)
        => HandlerResult<LoanPosition>.From(state with
        {
            LoanId = @event.LoanId,
            Lifecycle = LoanLifecycle.Failed,
        });
}

public sealed class LoanInstallmentPaidHandler : IEventHandler<LoanPosition, LoanInstallmentPaid>
{
    public HandlerResult<LoanPosition> Apply(LoanPosition state, LoanInstallmentPaid @event)
        => HandlerResult<LoanPosition>.From(state with
        {
            // The event carries the post-installment balance (computed by the decider off the
            // amortization schedule); the fold records it — no arithmetic, no rounding here.
            OutstandingBalance = @event.OutstandingBalance,
            InstallmentsPaid = state.InstallmentsPaid + 1,
            TotalInterestPaid = state.TotalInterestPaid + @event.Interest,
            TotalCapitalRepaid = state.TotalCapitalRepaid + @event.Capital,
        });
}

public sealed class LoanRepaidEarlyHandler : IEventHandler<LoanPosition, LoanRepaidEarly>
{
    public HandlerResult<LoanPosition> Apply(LoanPosition state, LoanRepaidEarly @event)
        => HandlerResult<LoanPosition>.From(state with
        {
            // The event carries the post-repayment balance (Money.Zero for a full repayment); the
            // capital repaid and the capped commission accumulate. The loan stays Active here — a FULL
            // repayment is CLOSED by a separate LoanSettled (the decider sequences them).
            OutstandingBalance = @event.OutstandingBalanceAfter,
            TotalCapitalRepaid = state.TotalCapitalRepaid + @event.CapitalRepaid,
            TotalCommissionCharged = state.TotalCommissionCharged + @event.Commission,
        });
}

public sealed class LoanSettledHandler : IEventHandler<LoanPosition, LoanSettled>
{
    public HandlerResult<LoanPosition> Apply(LoanPosition state, LoanSettled @event)
        => HandlerResult<LoanPosition>.From(state with
        {
            Lifecycle = LoanLifecycle.Settled,
        });
}

public sealed class LoanWrittenOffHandler : IEventHandler<LoanPosition, LoanWrittenOff>
{
    public HandlerResult<LoanPosition> Apply(LoanPosition state, LoanWrittenOff @event)
        => HandlerResult<LoanPosition>.From(state with
        {
            Lifecycle = LoanLifecycle.WrittenOff,
            // The written-off capital leaves the books: the outstanding balance is zeroed (no further
            // amortization), but the lost capital is RECORDED on the position as WrittenOffAmount rather
            // than dropped — it was NOT repaid, so it must not fold into TotalCapitalRepaid. Recording it
            // here is what disambiguates the write-off terminal from a fully-repaid one: both end with a
            // zero OutstandingBalance, so without this a written-off loan would be indistinguishable from
            // a settled one, and the lost principal would only be recoverable by replaying the event log
            // (bd babelstone-5r9n.8). With it, principal reconciles from the position alone:
            // TotalCapitalRepaid + WrittenOffAmount accounts for the original principal.
            OutstandingBalance = Money.Zero,
            WrittenOffAmount = state.WrittenOffAmount + @event.OutstandingBalanceWrittenOff,
        });
}
