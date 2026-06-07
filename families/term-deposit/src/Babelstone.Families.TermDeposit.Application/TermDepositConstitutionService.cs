using Babelstone.Engine;
using Babelstone.FinancialMath;
using Babelstone.Packs;
using Babelstone.RateSheets;

namespace Babelstone.Families.TermDeposit.Application;

/// <summary>
/// The term-deposit decider's impure orchestration (ADR-PC-021): it resolves the rate sheet
/// and pack primitives, calls the pure <see cref="TermDepositDecider"/>, settles the money leg,
/// and appends through the runtime. It depends only on generic engine ports
/// (<see cref="AggregateRuntime{TState}"/>, <see cref="IRateSheetStore"/>, <see cref="ISettlementPort"/>)
/// plus the pinned <see cref="VerifiedPack"/> — the dependency arrow is family→engine, never the
/// reverse (ADR-PC-021 §D2).
/// </summary>
/// <remarks>
/// The pinned <paramref name="pack"/> and its primitive bindings model the engine-instance's
/// pinned configuration for the walking skeleton (ADR-PC-009); a config registry resolving them
/// per deposit is later work. The resolve→append pair is two transactions here; the ADR-PC-008
/// §S2 in-transaction version is a tracked follow-up (bd babelstone-3k10). The shared
/// resolve→stamp→settle→append choreography is kept as separable steps so it can lift into a
/// generic ConstitutionPipeline on the second decider (ADR-PC-021 §P5, bd babelstone-osv6).
/// </remarks>
public sealed class TermDepositConstitutionService(
    AggregateRuntime<DepositPosition> runtime,
    IRateSheetStore rateSheets,
    ISettlementPort settlement,
    VerifiedPack pack,
    string dayCountPrimitive,
    string withholdingPrimitive)
{
    // The stream is keyed by the deposit id (v1: stream_id == deposit_id; partition_key == stream_id).
    private static readonly TermDepositFamilyModule Family = new();

    /// <summary>
    /// Constitute a deposit: resolve the active rate sheet, stamp the TAN + version id, debit
    /// the principal, and append <c>DepositConstituted</c> as the stream's first event. For the
    /// ADVANCE variant it ALSO accrues + withholds the full-term interest at t=0 and credits the
    /// upfront net to the funding account (02 §2.1 <c>CF(0) = -C + J</c>), appending the upfront
    /// <c>InterestPaid</c> triple alongside the constitution event in the same first transaction.
    /// </summary>
    public async Task ConstituteAsync(ConstituteDepositCommand command, CancellationToken ct = default)
    {
        // 1. Resolve the rate sheet active at constitution (ADR-PC-008 §P3); fail loud if none.
        var resolution = await rateSheets.ResolveAsync(Family.FamilyName, command.ConstitutedAt, ct)
            ?? throw new DomainRejectedException(
                $"No rate sheet effective for '{Family.FamilyName}' at {command.ConstitutedAt:O}.");

        // 2. Resolve the TAN for (product, role, principal); a null on a deployed sheet means the
        //    pair is genuinely unpriced — fail loud rather than constitute at a silent zero rate.
        var tan = resolution.ResolveTanBasisPoints(command.ProductId, command.Role, command.PrincipalCents)
            ?? throw new DomainRejectedException(
                $"Rate sheet '{resolution.RateSheetVersionId}' does not price " +
                $"({command.ProductId}, {command.Role}) at {command.PrincipalCents}c.");

        // 3. Decide (pure): build the event, stamping the resolved TAN + the version it came from.
        var constituted = TermDepositDecider.DecideConstitution(command, tan, resolution.RateSheetVersionId);

        // 4. Settle (ADR-PC-016): debit the principal from the funding account before recording it.
        await settlement.SettleAsync(
            new SettlementInstruction(
                command.DepositId, SettlementDirection.Debit, constituted.Principal,
                command.FundingAccount, "constitution"),
            ct);

        // 5. ADVANCE pays the full-term interest up front (t=0). Decide the upfront accrual+withholding
        //    off the just-constituted position, credit the net, and append the InterestPaid triple in
        //    the same first transaction as DepositConstituted (one atomic stream open).
        var events = new List<DomainEvent> { constituted };
        if (command.InterestVariant == TermDepositDecider.Advance)
        {
            var (dayCount, withholdingBps) = ResolvePrimitives();
            var position = FoldHead(constituted);
            var advance = TermDepositDecider.DecideAdvance(position, dayCount, withholdingBps);
            var paid = (InterestPaid)advance[^1];
            await settlement.SettleAsync(
                new SettlementInstruction(
                    command.DepositId, SettlementDirection.Credit, paid.NetInterest,
                    command.FundingAccount, "advance_interest"),
                ct);
            events.AddRange(advance);
        }

        // 6. Append the new stream (expectedVersion -1) — events + outbox in one transaction.
        await runtime.AppendAsync(
            command.DepositId, expectedVersion: -1, events,
            Context(command.Actor, command.ConstitutedAt), ct);
    }

    /// <summary>
    /// Mature a constituted deposit: rehydrate it, run the variant-branched maturity flow against
    /// the pinned pack's day-count and withholding, credit the payout, and append the closing events.
    /// AT_MATURITY matures the single full-term flow; PERIODIC matures the final coupon (principal +
    /// last coupon net); ADVANCE returns the principal alone (interest was paid at t=0). The branch
    /// lives in the pure decider (<see cref="TermDepositDecider.DecideMaturity"/>).
    /// </summary>
    public async Task MatureAsync(MatureDepositCommand command, CancellationToken ct = default)
    {
        // 1. Rehydrate the constituted position (load-then-append on the live stream head).
        var hydrated = await runtime.LoadAsync(command.DepositId, ct);
        var position = hydrated.State;

        // Transition-legality gate (F.3 state machine): maturing is legal only from Active. The
        // single LifecycleTransitions table — not a scattered inline check — decides, so maturing
        // a Matured/closed deposit is rejected uniformly with every other illegal transition.
        RejectIfIllegal(position.Lifecycle, LifecycleTransitions.Transition.Mature, command.DepositId, "mature");

        // 2. Pack-resolved primitives (fail loud, never a silent default).
        var (dayCount, withholdingBps) = ResolvePrimitives();

        // 3. Decide (pure): variant-branched accrue → withhold → mature.
        var events = TermDepositDecider.DecideMaturity(position, dayCount, withholdingBps);

        // 4. Settle (ADR-PC-016): credit the total payout. The DepositMatured event is the last.
        var matured = (DepositMatured)events[^1];
        await settlement.SettleAsync(
            new SettlementInstruction(
                command.DepositId, SettlementDirection.Credit, matured.TotalPayout,
                command.PayoutAccount, "maturity"),
            ct);

        // 5. Append at the current head (optimistic concurrency on the second append).
        await runtime.AppendAsync(
            command.DepositId, hydrated.Version, events,
            Context(command.Actor, command.MaturedAt), ct);
    }

    /// <summary>
    /// Pay one PERIODIC coupon: rehydrate the deposit (must be Active and PERIODIC), derive the NEXT
    /// coupon window from the start date, cadence, and the number of coupons already paid, run the
    /// pure <see cref="TermDepositDecider.DecideInterestPayment"/>, credit the coupon net, and append
    /// the <c>InterestPaid</c> triple. Coupons are triggered manually here, exactly as maturity is
    /// — the auto-firing time scheduler is deferred to A.8b. The final coupon is NOT paid here; it
    /// rides with the principal at maturity (<see cref="MatureAsync"/>'s PERIODIC branch), so a coupon
    /// whose window would reach the maturity date is rejected as "due at maturity".
    /// </summary>
    public async Task PayInterestAsync(PayInterestCommand command, CancellationToken ct = default)
    {
        // 1. Rehydrate; only an Active PERIODIC deposit pays coupons.
        var hydrated = await runtime.LoadAsync(command.DepositId, ct);
        var position = hydrated.State;

        // Transition-legality gate (F.3 state machine): paying a coupon is legal only from Active —
        // the table rejects paying on a Matured/closed deposit. The PERIODIC-only check below is a
        // separate VARIANT-applicability concern (an AT_MATURITY/ADVANCE deposit has no coupons),
        // not a lifecycle transition, so it stays outside the table.
        RejectIfIllegal(position.Lifecycle, LifecycleTransitions.Transition.PayInterest, command.DepositId, "pay interest");

        if (position.InterestVariant != TermDepositDecider.Periodic)
        {
            throw new DomainRejectedException(
                $"Deposit {command.DepositId} is {position.InterestVariant}, not PERIODIC; it has no coupons.");
        }

        // 2. Derive the next coupon window. Boundary k starts at (start + k cadences); the coupon
        //    just paid count is CouponsPaid, so the next window is [boundary(CouponsPaid) → boundary(CouponsPaid+1)].
        var periodStart = TermDepositDecider.CouponBoundary(position, position.CouponsPaid);
        var periodEnd = TermDepositDecider.CouponBoundary(position, position.CouponsPaid + 1);

        // 3. The final coupon is paid WITH the principal at maturity, never as a standalone coupon.
        //    If the next window already reaches maturity, there is no intermediate coupon left to pay.
        if (periodEnd >= position.MaturityDate)
        {
            throw new DomainRejectedException(
                $"Deposit {command.DepositId} has no intermediate coupon left to pay; " +
                "the final coupon is paid at maturity.");
        }

        // 4. Pack-resolved primitives (fail loud) and decide (pure).
        var (dayCount, withholdingBps) = ResolvePrimitives();
        var events = TermDepositDecider.DecideInterestPayment(
            position, periodStart, periodEnd, dayCount, withholdingBps);

        // 5. Settle (ADR-PC-016): credit the coupon net to the depositor's current account.
        var paid = (InterestPaid)events[^1];
        await settlement.SettleAsync(
            new SettlementInstruction(
                command.DepositId, SettlementDirection.Credit, paid.NetInterest,
                command.PayoutAccount, "coupon"),
            ct);

        // 6. Append at the current head (optimistic concurrency).
        await runtime.AppendAsync(
            command.DepositId, hydrated.Version, events,
            Context(command.Actor, command.PaidAt), ct);
    }

    /// <summary>
    /// Consult the F.3 lifecycle state machine (<see cref="LifecycleTransitions"/>) and reject an
    /// illegal transition with the established <see cref="DomainRejectedException"/> pattern. This is
    /// the single place command-side lifecycle legality is enforced — the scattered inline
    /// <c>if (Lifecycle != Active) throw</c> guards that used to live in the maturity/coupon flows now
    /// route through here, so the one auditable table (not duplicated literals) decides what is legal.
    /// </summary>
    /// <param name="current">The deposit's current folded lifecycle state.</param>
    /// <param name="transition">The transition the command would drive.</param>
    /// <param name="depositId">The deposit the command targets (for the rejection message).</param>
    /// <param name="action">A human verb for the rejection message, e.g. <c>"mature"</c>.</param>
    private static void RejectIfIllegal(
        DepositLifecycle current, LifecycleTransitions.Transition transition, Guid depositId, string action)
    {
        if (!LifecycleTransitions.IsLegal(current, transition))
        {
            throw new DomainRejectedException(
                $"Deposit {depositId} is {current}; cannot {action} (illegal lifecycle transition {transition}).");
        }
    }

    /// <summary>
    /// Pack-resolved primitives, fail-loud (never a silent default): the day-count convention and
    /// the withholding rate the deposit's pinned pack declares (ADR-PC-009).
    /// </summary>
    private (DayCountConvention DayCount, int WithholdingBps) ResolvePrimitives()
    {
        var dayCount = pack.ResolveDayCount(dayCountPrimitive);
        var withholdingBps = pack.Withholdings.TryGetValue(withholdingPrimitive, out var withholding)
            ? withholding.RateBasisPoints
            : throw new InvalidOperationException(
                $"Withholding primitive '{withholdingPrimitive}' is not declared in pack {pack.VersionKey}.");
        return (dayCount, withholdingBps);
    }

    /// <summary>The head position the ADVANCE upfront accrual decides against — the just-constituted
    /// deposit, before any interest. The decider only reads Principal/TanBasisPoints/StartDate/
    /// MaturityDate/DepositId for ADVANCE, so we project exactly those off the constitution event
    /// (the same values <see cref="DepositConstitutedHandler"/> folds). No durable round-trip
    /// mid-transaction; no clock.</summary>
    private static DepositPosition FoldHead(DepositConstituted constituted) =>
        DepositPosition.Empty with
        {
            DepositId = constituted.DepositId,
            Principal = constituted.Principal,
            TanBasisPoints = constituted.TanBasisPoints,
            RateSheetVersionId = constituted.RateSheetVersionId,
            TermDays = constituted.TermDays,
            StartDate = constituted.StartDate,
            MaturityDate = constituted.MaturityDate,
            InterestVariant = constituted.InterestVariant,
            AutoRenewalPolicy = constituted.AutoRenewalPolicy,
            PaymentPeriodMonths = constituted.PaymentPeriodMonths,
            RemainingPrincipal = constituted.Principal,
            Lifecycle = DepositLifecycle.Active,
        };

    private AppendContext Context(string actor, DateTimeOffset validTime) =>
        new(Family.FamilyName, pack.VersionKey, Family.SchemaVersion, actor, validTime);
}
