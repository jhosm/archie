using Babelstone.Engine;
using Babelstone.FinancialMath;
using Babelstone.Packs;
using Babelstone.RateSheets;

namespace Babelstone.Families.TermDeposit.Application;

/// <summary>
/// The term-deposit decider's impure orchestration (ADR-PC-021): it resolves the rate sheet
/// and pack primitives, calls the pure <see cref="TermDepositDecider"/>, settles the money leg
/// (for the lifecycle steps whose own sagas have not yet landed — constitution is now DE-SETTLED,
/// bd babelstone-t7o3.4), and appends through the runtime. It depends only on generic engine ports
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
    string withholdingPrimitive,
    EarlyTerminationPolicy? earlyTerminationPolicy = null,
    IReadOnlyCollection<string>? requiredPreconditions = null)
{
    // The stream is keyed by the deposit id (v1: stream_id == deposit_id; partition_key == stream_id).
    private static readonly TermDepositFamilyModule Family = new();

    // The product's required commercial-eligibility preconditions (ADR-PC-024 §1, from the product
    // config's `required_preconditions`). Engine-instance config for the walking skeleton, mirroring
    // the pinned-pack / early-termination-policy stand-ins (ADR-PC-009): a per-deposit config registry
    // resolving it is later work. Defaults to an empty set — v1 launch products are NOT eligibility-
    // gated (02 §4), so the common path never refuses on preconditions.
    private readonly IReadOnlyCollection<string> _requiredPreconditions =
        requiredPreconditions ?? Array.Empty<string>();

    /// <summary>
    /// Constitute a deposit: resolve the active rate sheet, stamp the TAN + version id, and append
    /// <c>DepositConstituted</c> as the stream's first event. The path is DE-SETTLED (bd
    /// babelstone-t7o3.4): the principal debit is NOT done here — it is the constitution saga's gated
    /// step (ReserveAccountBalance→ConfirmDebit against the Core ACL, ADR-PC-016 §68/§127 / ADR-PC-029
    /// slot 2). The engine command DECIDES + APPENDS only; no money leg rides this path. For the ADVANCE
    /// variant it ALSO accrues + withholds the full-term interest at t=0 (02 §2.1 <c>CF(0) = -C + J</c>)
    /// and appends the upfront <c>InterestPaid</c> triple alongside the constitution event in the same
    /// first transaction — the interest IS recognised in the engine's books at t=0, but its money leg is
    /// likewise the saga's gated credit, not an eager in-engine settle.
    /// </summary>
    /// <returns>The new stream's head version (ADR-IC-005 §P3 read-your-writes token / commit_sequence).</returns>
    public async Task<long> ConstituteAsync(ConstituteDepositCommand command, CancellationToken ct = default)
    {
        // 0. Commercial-eligibility gate (ADR-PC-024 §5): refuse BEFORE any rate-sheet resolve or the
        //    irreversible Core debit, as a PURE function of the command's resolved verdicts — no upstream
        //    call, no in-engine evaluation. A required precondition that is absent or satisfied:false
        //    yields DepositConstitutionFailed (reason ELIGIBILITY_NOT_MET) appended as the stream's first
        //    and only event; no deposit is opened, so there is nothing to unwind (it is a refusal, not a
        //    compensation). The verdicts the saga gathered ride on the command (ADR-PC-024 §3).
        var refusal = TermDepositDecider.CheckPreconditions(
            command.DepositId, _requiredPreconditions, command.Preconditions);
        if (refusal is not null)
        {
            return await runtime.AppendAsync(
                command.DepositId, expectedVersion: -1, [refusal],
                Context(command.Actor, command.ConstitutedAt, command.CommandId), ct);
        }

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

        // 4. DE-SETTLED constitution (bd babelstone-t7o3.4, ADR-PC-016 §68/§127). The engine no longer
        //    debits the funding account on this path: settlement is the constitution SAGA's GATED step
        //    (the orchestrator decides ReserveAccountBalance→ConfirmDebit, the dispatcher delivers them
        //    to the Core ACL — ADR-PC-029 slot 2 "the engine-bound command is de-settled: it appends
        //    only"). The engine command DECIDES + APPENDS only — no money leg rides this path. The other
        //    lifecycle steps (maturity, coupon, early termination, renewal) still settle eagerly here
        //    until their own sagas land; only the constitution path is relocated.
        //
        // 5. ADVANCE recognises the full-term interest at t=0 (CF(0) = -C + J). The upfront InterestPaid
        //    TRIPLE is still appended in the same first transaction (the interest IS recognised in the
        //    engine's books at constitution) — but its MONEY leg, like the principal debit, is now the
        //    saga's gated credit, not an eager in-engine settle. We decide the triple (pure) and append it;
        //    we do NOT call settlement here.
        var events = new List<DomainEvent> { constituted };
        if (command.InterestVariant == TermDepositDecider.Advance)
        {
            var (dayCount, withholdingBps) = ResolvePrimitives();
            var position = FoldHead(constituted);
            var advance = TermDepositDecider.DecideAdvance(position, dayCount, withholdingBps);
            events.AddRange(advance);
        }

        // 6. Append the new stream (expectedVersion -1) — events + outbox in one transaction. The head
        //    version it returns is the commit_sequence the caller threads for read-your-writes. The
        //    command id (when supplied) makes this append idempotent: a replay returns this same head
        //    with no second append (ADR-PC-029 slot 4).
        return await runtime.AppendAsync(
            command.DepositId, expectedVersion: -1, events,
            Context(command.Actor, command.ConstitutedAt, command.CommandId), ct);
    }

    /// <summary>
    /// Mature a constituted deposit: rehydrate it, run the variant-branched maturity flow against
    /// the pinned pack's day-count and withholding, credit the payout, and append the closing events.
    /// AT_MATURITY matures the single full-term flow; PERIODIC matures the final coupon (principal +
    /// last coupon net); ADVANCE returns the principal alone (interest was paid at t=0). The branch
    /// lives in the pure decider (<see cref="TermDepositDecider.DecideMaturity"/>).
    /// </summary>
    /// <returns>The stream's head version after maturity (ADR-IC-005 §P3 read-your-writes token / commit_sequence).</returns>
    public async Task<long> MatureAsync(MatureDepositCommand command, CancellationToken ct = default)
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

        // 5. Append at the current head (optimistic concurrency on the second append). The returned
        //    head version is the commit_sequence the caller threads for read-your-writes.
        return await runtime.AppendAsync(
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
    /// <returns>The stream's head version after the coupon (ADR-IC-005 §P3 read-your-writes token / commit_sequence).</returns>
    public async Task<long> PayInterestAsync(PayInterestCommand command, CancellationToken ct = default)
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

        // 6. Append at the current head (optimistic concurrency). The returned head version is the
        //    commit_sequence the caller threads for read-your-writes.
        return await runtime.AppendAsync(
            command.DepositId, hydrated.Version, events,
            Context(command.Actor, command.PaidAt), ct);
    }

    /// <summary>
    /// Break a constituted deposit before maturity (02 §2.5): rehydrate it (must be Active — the F.3
    /// gate decides), accrue the elapsed-period interest, withhold that one flow, apply the product's
    /// configured penalty (flat or banded, to the right basis, respecting the floor), credit the net
    /// settlement, and append the closing events. The penalty math lives in the pure decider
    /// (<see cref="TermDepositDecider.DecideEarlyTermination"/>); withholding is flow-by-flow on the one
    /// accrued flow, never rate-scaled. Termination is triggered MANUALLY here, exactly as maturity is.
    /// </summary>
    public async Task TerminateEarlyAsync(TerminateEarlyCommand command, CancellationToken ct = default)
    {
        // 0. The product's early-termination policy is engine-instance config for the walking skeleton
        //    (mirroring the pinned pack stand-in, ADR-PC-009). Fail loud if no policy is configured —
        //    never settle a break at a silent zero penalty.
        var policy = earlyTerminationPolicy
            ?? throw new InvalidOperationException(
                "No early-termination policy is configured for this engine instance; refusing to terminate " +
                $"deposit {command.DepositId} without a policy (02 §2.5).");

        // 1. Rehydrate the constituted position (load-then-append on the live stream head).
        var hydrated = await runtime.LoadAsync(command.DepositId, ct);
        var position = hydrated.State;

        // Transition-legality gate (F.3 state machine): breaking early is legal only from Active. The
        // single LifecycleTransitions table — not a scattered inline check — decides, so terminating a
        // Matured/closed deposit is rejected uniformly with every other illegal transition.
        RejectIfIllegal(position.Lifecycle, LifecycleTransitions.Transition.TerminateEarly, command.DepositId, "terminate early");

        // 2. Pack-resolved primitives (fail loud, never a silent default).
        var (dayCount, withholdingBps) = ResolvePrimitives();

        // 3. Decide (pure): accrue the elapsed flow → withhold → penalty → floor → settle. The
        //    termination DATE is derived from the command instant and passed as an INPUT — no clock
        //    in the decider.
        var terminationDate = DateOnly.FromDateTime(command.TerminatedAt.UtcDateTime);
        var events = TermDepositDecider.DecideEarlyTermination(
            position, terminationDate, policy, dayCount, withholdingBps, command.TerminationReason);

        // 4. Settle (ADR-PC-016): credit the net settlement to the depositor's current account. The
        //    DepositTerminatedEarly event is the last and carries the settlement amount.
        var terminated = (DepositTerminatedEarly)events[^1];
        await settlement.SettleAsync(
            new SettlementInstruction(
                command.DepositId, SettlementDirection.Credit, terminated.NetSettlementAmount,
                command.PayoutAccount, "early_termination"),
            ct);

        // 5. Append at the current head (optimistic concurrency on the second append).
        await runtime.AppendAsync(
            command.DepositId, hydrated.Version, events,
            Context(command.Actor, command.TerminatedAt), ct);
    }

    /// <summary>
    /// Auto-renew a maturing deposit (02 §2.4.4): emit <c>DepositMatured</c> for the closing instance,
    /// constitute a fresh engine-native instance from the rolled-over principal, and link them with
    /// <c>DepositRenewed</c> — the three events in that exact order. The maturity leg mirrors
    /// <see cref="MatureAsync"/> (variant-branched accrue → withhold → mature, flow-by-flow withholding);
    /// the renewal branches on the closing deposit's <c>auto_renewal_policy</c> for the new rate, and the
    /// new <c>DepositConstituted</c> carries <c>causation_id</c> = the closing <c>DepositMatured</c>'s
    /// event id (02 §2.4.4 step 2). Renewal is triggered MANUALLY here; the time scheduler is H.3.
    /// </summary>
    /// <remarks>
    /// The three events span TWO streams, so they cannot commit in one append: <c>DepositMatured</c> and
    /// <c>DepositRenewed</c> fold the closing deposit's stream, while the new <c>DepositConstituted</c>
    /// opens a fresh stream keyed by <see cref="RenewDepositCommand.NewDepositId"/>. They are appended in
    /// three steps to honour both the event order AND the causation link: the maturity leg appends first
    /// (so its <c>DepositMatured</c> event id exists), then the new constitution opens its stream with that
    /// id as <c>causation_id</c>, then <c>DepositRenewed</c> closes the old stream. The walking skeleton has
    /// no cross-stream transaction; a renewal saga making the three-step append crash-atomic is later work
    /// (tracked under bd babelstone-k4yr).
    /// </remarks>
    public async Task RenewAsync(RenewDepositCommand command, CancellationToken ct = default)
    {
        // 1. Rehydrate the closing deposit (must be Active to renew — the F.3 gate decides). The
        //    renewal drives TWO transitions on this stream — Mature (step 5) then Renew (step 9) —
        //    and each routes through the F.3 table at the point it fires (the table is the single
        //    legality authority; no inline lifecycle literal lives here). This entry gate is the
        //    Renew-intent check: Renew is legal only from Active, so it rejects renewing a closed
        //    (e.g. already-Renewed/Matured) deposit before any work, sheet resolve, or settlement.
        var hydrated = await runtime.LoadAsync(command.DepositId, ct);
        var closing = hydrated.State;
        RejectIfIllegal(closing.Lifecycle, LifecycleTransitions.Transition.Renew, command.DepositId, "renew");

        // 2. NONE never auto-renews: there is no new instance to constitute (02 §2.4.4). Reject loud
        //    rather than silently fall through — only SAME_TERM_* policies reach the renewal flow.
        if (closing.AutoRenewalPolicy == TermDepositDecider.RenewalNone)
        {
            throw new DomainRejectedException(
                $"Deposit {command.DepositId} has auto_renewal_policy NONE; it terminates at maturity, never renews.");
        }

        // 3. Opt-out window (02 §2.4.4): the depositor's final auto_renewal_optout_window_days before
        //    maturity is when a customer-initiated termination still blocks the renewal without penalty.
        //    The engine enforces that auto-renewal does NOT fire before that window has elapsed — i.e. not
        //    before the maturity date — so a renewal triggered while the opt-out right is still open is
        //    rejected. The window length is a pack parameter (parsed into PackParameters), read fail-loud.
        var renewalDate = DateOnly.FromDateTime(command.RenewedAt.UtcDateTime);
        var optOutWindowOpens = closing.MaturityDate.AddDays(-pack.Parameters.AutoRenewalOptoutWindowDays);
        if (renewalDate < closing.MaturityDate)
        {
            var reason = renewalDate >= optOutWindowOpens
                ? $"within the {pack.Parameters.AutoRenewalOptoutWindowDays}-day pre-maturity opt-out window"
                : "before maturity";
            throw new DomainRejectedException(
                $"Deposit {command.DepositId} cannot auto-renew on {renewalDate:O} ({reason}); the opt-out " +
                $"window closes at maturity {closing.MaturityDate:O} and renewal fires no earlier.");
        }

        // 4. Pack-resolved primitives (fail loud) and the maturity leg — the SAME variant-branched
        //    accrue → withhold → mature the standalone MatureAsync runs (flow-by-flow withholding).
        //    Renewal's maturity leg appends DepositMatured (folds the closing stream to Matured), so
        //    it drives the very same Mature transition MatureAsync gates — route it through the F.3
        //    table here too, BEFORE appending, so the one auditable table (not a scattered guard)
        //    decides Mature legality on EVERY path. closing is the Active head loaded at step 1, so
        //    this checks Mature-from-Active exactly as the standalone command does.
        RejectIfIllegal(closing.Lifecycle, LifecycleTransitions.Transition.Mature, command.DepositId, "mature");
        var (dayCount, withholdingBps) = ResolvePrimitives();
        var maturityEvents = TermDepositDecider.DecideMaturity(closing, dayCount, withholdingBps);
        var matured = (DepositMatured)maturityEvents[^1];

        // 5. Settle the closing maturity payout (mirrors MatureAsync), then append the maturity leg as
        //    step 1 of the renewal. The principal settles out here and back in at the new constitution, so
        //    each leg's money movement matches its standalone command.
        await settlement.SettleAsync(
            new SettlementInstruction(
                command.DepositId, SettlementDirection.Credit, matured.TotalPayout,
                command.PayoutAccount, "maturity"),
            ct);
        await runtime.AppendAsync(
            command.DepositId, hydrated.Version, maturityEvents,
            Context(command.Actor, command.RenewedAt), ct);

        // The closing DepositMatured is now the closing stream's head; its event id is the causation
        // root for the new instance (02 §2.4.4 step 2). Reload to capture it (AppendAsync returns void).
        var afterMaturity = await runtime.LoadAsync(command.DepositId, ct);
        var maturedEventId = afterMaturity.LastEventId;

        // 6. Resolve the renewal rate by policy: SAME_TERM_CURRENT_RATE re-resolves the sheet at the
        //    renewal moment (the bank's then-current standard rate); SAME_TERM_SAME_RATE carries the
        //    original rate (the pure decider picks). Re-resolution is fail-loud, exactly as constitution.
        int renewalTan;
        string renewalRateSheetVersionId;
        if (closing.AutoRenewalPolicy == TermDepositDecider.RenewalSameTermCurrentRate)
        {
            var resolution = await rateSheets.ResolveAsync(Family.FamilyName, command.RenewedAt, ct)
                ?? throw new DomainRejectedException(
                    $"No rate sheet effective for '{Family.FamilyName}' at {command.RenewedAt:O} to renew {command.DepositId}.");
            var currentTan = resolution.ResolveTanBasisPoints(command.ProductId, command.Role, closing.RemainingPrincipal.Cents)
                ?? throw new DomainRejectedException(
                    $"Rate sheet '{resolution.RateSheetVersionId}' does not price " +
                    $"({command.ProductId}, {command.Role}) at {closing.RemainingPrincipal.Cents}c to renew {command.DepositId}.");
            (renewalTan, renewalRateSheetVersionId) =
                TermDepositDecider.ResolveRenewalRate(closing, currentTan, resolution.RateSheetVersionId);
        }
        else
        {
            // SAME_TERM_SAME_RATE: no re-resolution — the decider carries the closing rate forward.
            (renewalTan, renewalRateSheetVersionId) =
                TermDepositDecider.ResolveRenewalRate(closing, closing.TanBasisPoints, closing.RateSheetVersionId);
        }

        // 7. Decide (pure): the new constitution from the rolled-over principal at the policy-resolved
        //    rate, for the same term/variant/cadence/policy as the closing deposit.
        var renewed = TermDepositDecider.DecideRenewalConstitution(
            closing, command.NewDepositId, closing.RemainingPrincipal, renewalTan, renewalRateSheetVersionId, renewalDate);

        // 8. Settle the rolled-over principal into the new instance (mirrors ConstituteAsync's debit),
        //    then open the new stream (expectedVersion -1) as step 2 — its causation_id roots at the
        //    closing DepositMatured. ADVANCE pays its full-term interest up front here, same as a fresh
        //    constitution; the upfront InterestPaid rides in the new stream's first transaction.
        await settlement.SettleAsync(
            new SettlementInstruction(
                command.NewDepositId, SettlementDirection.Debit, renewed.Principal,
                command.FundingAccount, "renewal_rollover"),
            ct);
        var renewalConstitutionEvents = new List<DomainEvent> { renewed };
        if (renewed.InterestVariant == TermDepositDecider.Advance)
        {
            var newPosition = FoldHead(renewed);
            var advance = TermDepositDecider.DecideAdvance(newPosition, dayCount, withholdingBps);
            var paid = (InterestPaid)advance[^1];
            await settlement.SettleAsync(
                new SettlementInstruction(
                    command.NewDepositId, SettlementDirection.Credit, paid.NetInterest,
                    command.FundingAccount, "advance_interest"),
                ct);
            renewalConstitutionEvents.AddRange(advance);
        }

        await runtime.AppendAsync(
            command.NewDepositId, expectedVersion: -1, renewalConstitutionEvents,
            Context(command.Actor, command.RenewedAt) with { CausationId = maturedEventId }, ct);

        // 9. Decide (pure) and append the DepositRenewed link as step 3, closing the old stream. It folds
        //    the closing deposit to Renewed (terminal) — the old→new lookup the maturity calendar uses.
        //    This append is NOT re-gated against the table: the stream head is now Matured (step 5 folded
        //    it), and the F.3 table (babelstone-29v8) models Matured as terminal — Renew is legal only from
        //    Active, with terminality expressed as absence from every source set. The spec-mandated closing
        //    sequence (02 §2.4.4: DepositMatured THEN DepositRenewed) thus legally traverses
        //    Active→Matured→Renewed, which the table cannot currently express (no Renew-from-Matured row).
        //    The Renew legality IS checked once, at the step-1 entry gate while the deposit is still Active —
        //    the only state the table makes Renew legal from. Making the table express this compound
        //    sequence (a Renew-from-Matured row, or modelling renewal as a single Active→Renewed transition
        //    whose DepositMatured is intrinsic) is an F.3 modelling decision tracked on babelstone-29v8;
        //    until then this leg stays as the spec dictates rather than forcing F.3's terminal model open.
        var link = TermDepositDecider.DecideRenewalLink(closing, renewed);
        await runtime.AppendAsync(
            command.DepositId, afterMaturity.Version, [link],
            Context(command.Actor, command.RenewedAt), ct);
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

    // commandId is the OPTIONAL command-ingress idempotency key (ADR-PC-029 slot 4): the
    // constitution paths thread the command's CommandId so the append dedupes on it; the
    // engine-internal lifecycle steps (maturity, coupon, termination, renewal) pass none.
    private AppendContext Context(string actor, DateTimeOffset validTime, Guid? commandId = null) =>
        new(Family.FamilyName, pack.VersionKey, Family.SchemaVersion, actor, validTime, CommandId: commandId);
}
