using Babelstone.Engine;
using Babelstone.FinancialTypes;

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
            // The pricing role + opaque funding-account token carried onto the event
            // (bd babelstone-mtto.5); "" for pre-mtto.5 deposits whose event decoded the Avro
            // default. Copied verbatim — no clock, no I/O, no derivation — so the engine can
            // re-resolve the renewal rate + settle the rollover debit from the closing deposit
            // (the fold stays pure/deterministic, BENG001/002/003).
            Role = @event.Role,
            FundingAccount = @event.FundingAccount,
            // The product-config generation pin (ADR-PC-009 §A2); "" for pre-pin deposits / no store
            // wired (the Avro default). Copied verbatim — no clock, no I/O, no derivation — so a cold
            // replay re-derives the identical pin (REPLAY_PIN_PER_EVENT, BENG001/002/003).
            ProductConfigVersion = @event.ProductConfigVersion,
            // The F.12 partial-withdrawal policy PINNED at constitution (bd k6r8.8/qze9), copied
            // verbatim onto the position — no clock, no I/O, no derivation (BENG001/002/003). The
            // partial-withdrawal command path rebuilds the policy from these three folded fields, so
            // a later config edit can never retroactively change a live deposit's withdrawal rights.
            // 0/0/0 (the Avro default a pre-F.12 record decodes) ⇒ the Unrestricted policy.
            MinWithdrawalCents = @event.MinWithdrawalCents,
            MinRemainingBalanceCents = @event.MinRemainingBalanceCents,
            LockupPeriodDays = @event.LockupPeriodDays,
            // RemainingPrincipal tracks principal still on deposit; it starts at the full
            // principal and is reduced by partial withdrawals (the event carries the result).
            RemainingPrincipal = @event.Principal,
            // Seed the principal timeline (F.12, bd babelstone-emtr) with the opening segment: the full
            // principal in force from the deposit's start. Each partial withdrawal appends a segment, so
            // accrual prices interest on the principal actually held over each sub-period. Pure — a
            // single new list from the event's own fields, no clock/I/O (BENG001/002/003).
            PrincipalTimeline = [new PrincipalSegment(@event.StartDate, @event.Principal)],
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

// The undeliverable-payout pair (ADR-PC-043 slot 5): the matured deposit holds
// the payout AT SOURCE when it cannot land, then resolves back once a live destination exists. Same
// purity contract as the folds above — a single `state with { … }`, no clock/I/O/randomness
// (BENG001/002/003); each LABELS lifecycle only. Transition legality is F.3 (LifecycleTransitions).

public sealed class DepositPayoutHeldHandler : IEventHandler<DepositPosition, DepositPayoutHeld>
{
    public HandlerResult<DepositPosition> Apply(DepositPosition state, DepositPayoutHeld @event)
        => HandlerResult<DepositPosition>.From(state with
        {
            // Held at source: mark the deposit payout-pending. The undeliverable credit's attributed IOU
            // is the engine's cross-cutting operations.CreditUnapplied — this only labels the source.
            Lifecycle = DepositLifecycle.PayoutPending,
        });
}

public sealed class DepositPayoutLandedHandler : IEventHandler<DepositPosition, DepositPayoutLanded>
{
    public HandlerResult<DepositPosition> Apply(DepositPosition state, DepositPayoutLanded @event)
        => HandlerResult<DepositPosition>.From(state with
        {
            // Resolved: the re-attempt landed, so the deposit reaches its settled maturity terminal.
            Lifecycle = DepositLifecycle.Matured,
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
            // Append a principal-timeline segment (F.12, bd babelstone-emtr): from this withdrawal's
            // date onward the deposit holds the reduced principal, so later coupons and the maturity
            // payout accrue/return on it (and the days before this withdrawal stay priced on the prior
            // principal). Pure list-spread, no clock/I/O (BENG001/002/003).
            PrincipalTimeline = [.. state.PrincipalTimeline, new PrincipalSegment(@event.WithdrawnOn, @event.RemainingPrincipal)],
        });
}

public sealed class DepositCorrectedHandler : IEventHandler<DepositPosition, DepositCorrected>
{
    public HandlerResult<DepositPosition> Apply(DepositPosition state, DepositCorrected @event)
    {
        // Typed inline value substitution (bd babelstone-j7mm.2; ADR-PC-002 §P2 *Revised 2026-06-27*).
        // A data-entry correction means "this is what the value ALWAYS was": substitute the corrected
        // typed structural value the event carries (the field CorrectedField names; the others are null)
        // so a query reads back the CORRECTED value, not just a bumped counter. PURE — the event carries
        // the value, the fold only assigns it (no clock/I/O/derivation, BENG001/002/003), so cold replay
        // is byte-identical. PROSPECTIVE (L2): future accrual/withholding/payout naturally price on the
        // corrected value; retroactive recompute of already-crystallized flows is L3 (bd babelstone-np7p).
        var corrected = state with
        {
            TanBasisPoints = @event.CorrectedTanBasisPoints ?? state.TanBasisPoints,
            StartDate = @event.CorrectedStartDate ?? state.StartDate,
            MaturityDate = @event.CorrectedMaturityDate ?? state.MaturityDate,
            CorrectionCount = state.CorrectionCount + 1,
        };

        if (@event.CorrectedPrincipal is { } correctedPrincipal)
        {
            corrected = corrected with
            {
                Principal = correctedPrincipal,
                // "What it always was": rewrite the OPENING principal-timeline segment's amount (keeping its
                // value-date) so future accrual prices on the corrected principal — NOT a step-change at
                // effective_from (that is a partial withdrawal). A single-segment timeline (no partial
                // withdrawal) means remaining principal equals the corrected principal; with a later
                // withdrawal segment the residual is a retroactive recompute (L3), so leave it then.
                RemainingPrincipal = state.PrincipalTimeline.Count <= 1 ? correctedPrincipal : state.RemainingPrincipal,
                PrincipalTimeline = RewriteOpeningPrincipal(state.PrincipalTimeline, correctedPrincipal),
            };
        }

        if (@event.CorrectedStartDate is { } correctedStart)
        {
            // A corrected start_date rewrites the opening segment's value-date too (the date the principal
            // was first in force), keeping the accrual windows coherent with the corrected start.
            corrected = corrected with
            {
                PrincipalTimeline = RewriteOpeningFrom(corrected.PrincipalTimeline, correctedStart),
            };
        }

        return HandlerResult<DepositPosition>.From(corrected);
    }

    private static IReadOnlyList<PrincipalSegment> RewriteOpeningPrincipal(
        IReadOnlyList<PrincipalSegment> timeline, Money correctedPrincipal) =>
        timeline.Count == 0
            ? timeline
            : [timeline[0] with { Principal = correctedPrincipal }, .. timeline.Skip(1)];

    private static IReadOnlyList<PrincipalSegment> RewriteOpeningFrom(
        IReadOnlyList<PrincipalSegment> timeline, DateOnly correctedFrom) =>
        timeline.Count == 0
            ? timeline
            : [timeline[0] with { From = correctedFrom }, .. timeline.Skip(1)];
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
