using Babelstone.Engine;

namespace Babelstone.Families.TermDeposit;

// Pure folds (state, event) → state — one per event, mirroring CounterFamily. No clock,
// no I/O, no randomness (BENG001/002/003); each body is a single `state with { … }`. The
// money sums use Money's own checked + operator (no decimal state, no mid-step rounding).
// Sums accumulate (state.X + event.Y) rather than overwrite, so the fold stays correct
// under replay even when a future variant emits multiple accrual/withholding flows.

public sealed class DepositConstitutedHandler : IEventHandler<DepositPosition, DepositConstituted>
{
    public HandlerResult<DepositPosition> Apply(DepositPosition state, DepositConstituted @event)
        => HandlerResult<DepositPosition>.From(state with
        {
            DepositId = @event.DepositId,
            Principal = @event.Principal,
            TanBasisPoints = @event.TanBasisPoints,
            RateSheetVersionId = @event.RateSheetVersionId,
            TermDays = @event.TermDays,
            StartDate = @event.StartDate,
            MaturityDate = @event.MaturityDate,
            InterestVariant = @event.InterestVariant,
            AutoRenewalPolicy = @event.AutoRenewalPolicy,
            Lifecycle = DepositLifecycle.Active,
        });
}

public sealed class InterestAccruedHandler : IEventHandler<DepositPosition, InterestAccrued>
{
    public HandlerResult<DepositPosition> Apply(DepositPosition state, InterestAccrued @event)
        => HandlerResult<DepositPosition>.From(state with
        {
            AccruedGrossInterest = state.AccruedGrossInterest + @event.GrossInterest,
        });
}

public sealed class WithholdingAppliedHandler : IEventHandler<DepositPosition, WithholdingApplied>
{
    public HandlerResult<DepositPosition> Apply(DepositPosition state, WithholdingApplied @event)
        => HandlerResult<DepositPosition>.From(state with
        {
            WithholdingToDate = state.WithholdingToDate + @event.Tax,
            NetInterest = state.NetInterest + @event.Net,
        });
}

public sealed class DepositMaturedHandler : IEventHandler<DepositPosition, DepositMatured>
{
    public HandlerResult<DepositPosition> Apply(DepositPosition state, DepositMatured @event)
        => HandlerResult<DepositPosition>.From(state with
        {
            Lifecycle = DepositLifecycle.Matured,
            TotalPayout = @event.TotalPayout,
        });
}
