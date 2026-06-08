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
            PaymentPeriodMonths = @event.PaymentPeriodMonths,
            // The catalogue product code carried onto the event (bd babelstone-v794); "" for
            // pre-v794 deposits whose event decoded the Avro default. Copied verbatim — no clock,
            // no I/O, no derivation, so the fold stays pure/deterministic (BENG001/002/003).
            ProductCode = @event.ProductCode,
            // RemainingPrincipal tracks principal still on deposit; it starts at the full
            // principal and is reduced by partial withdrawals (the event carries the result).
            RemainingPrincipal = @event.Principal,
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

// The seven remaining folds (F.2, babelstone-5czr). Same purity contract as the four above:
// each body is a single `state with { … }`, no clock/I/O/randomness (BENG001/002/003). These
// LABEL lifecycle and accumulate carried facts; transition legality is F.3 (babelstone-29v8).

public sealed class DepositConstitutionFailedHandler : IEventHandler<DepositPosition, DepositConstitutionFailed>
{
    public HandlerResult<DepositPosition> Apply(DepositPosition state, DepositConstitutionFailed @event)
        => HandlerResult<DepositPosition>.From(state with
        {
            DepositId = @event.DepositId,
            Lifecycle = DepositLifecycle.Failed,
        });
}

public sealed class InterestPaidHandler : IEventHandler<DepositPosition, InterestPaid>
{
    public HandlerResult<DepositPosition> Apply(DepositPosition state, InterestPaid @event)
        => HandlerResult<DepositPosition>.From(state with
        {
            // Periodic payout: accumulate the same gross/withholding/net tallies the
            // AT_MATURITY flow feeds, so the position stays correct across multiple coupons.
            AccruedGrossInterest = state.AccruedGrossInterest + @event.GrossInterest,
            WithholdingToDate = state.WithholdingToDate + @event.WithholdingTax,
            NetInterest = state.NetInterest + @event.NetInterest,
            // Count coupons so the service can derive the next coupon window deterministically
            // (start + cadence × CouponsPaid) without a clock in the fold. The ADVANCE upfront
            // payment also folds here; its single InterestPaid leaves CouponsPaid at 1, harmless
            // because ADVANCE never pays further coupons (the service gates on the variant).
            CouponsPaid = state.CouponsPaid + 1,
        });
}

public sealed class DepositRenewedHandler : IEventHandler<DepositPosition, DepositRenewed>
{
    public HandlerResult<DepositPosition> Apply(DepositPosition state, DepositRenewed @event)
        => HandlerResult<DepositPosition>.From(state with
        {
            Lifecycle = DepositLifecycle.Renewed,
        });
}

public sealed class DepositTerminatedEarlyHandler : IEventHandler<DepositPosition, DepositTerminatedEarly>
{
    public HandlerResult<DepositPosition> Apply(DepositPosition state, DepositTerminatedEarly @event)
        => HandlerResult<DepositPosition>.From(state with
        {
            Lifecycle = DepositLifecycle.TerminatedEarly,
            SettlementAmount = @event.NetSettlementAmount,
        });
}

public sealed class DepositPartiallyWithdrawnHandler : IEventHandler<DepositPosition, DepositPartiallyWithdrawn>
{
    public HandlerResult<DepositPosition> Apply(DepositPosition state, DepositPartiallyWithdrawn @event)
        => HandlerResult<DepositPosition>.From(state with
        {
            // The event carries the post-withdrawal principal (computed by the decider);
            // the fold just records it — no arithmetic, no rounding here.
            RemainingPrincipal = @event.RemainingPrincipal,
        });
}

public sealed class DepositCorrectedHandler : IEventHandler<DepositPosition, DepositCorrected>
{
    public HandlerResult<DepositPosition> Apply(DepositPosition state, DepositCorrected @event)
        => HandlerResult<DepositPosition>.From(state with
        {
            // The real bitemporal supersession is D.1/D.2; here the fold only tallies that a
            // correction landed (references resolved elsewhere), keeping the handler pure.
            CorrectionCount = state.CorrectionCount + 1,
        });
}

public sealed class DepositTransferredToHeirsHandler : IEventHandler<DepositPosition, DepositTransferredToHeirs>
{
    public HandlerResult<DepositPosition> Apply(DepositPosition state, DepositTransferredToHeirs @event)
        => HandlerResult<DepositPosition>.From(state with
        {
            Lifecycle = DepositLifecycle.TransferredToHeirs,
            SettlementAmount = @event.TransferredBalance,
        });
}
