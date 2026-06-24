using Babelstone.Engine;
using Babelstone.FinancialMath;
using Babelstone.FinancialTypes;
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
    IReadOnlyCollection<string>? requiredPreconditions = null,
    IProductConfigStore? productConfigStore = null)
{
    // The stream is keyed by the deposit id (v1: stream_id == deposit_id; partition_key == stream_id).
    private static readonly TermDepositFamilyModule Family = new();

    // The engine-side product-config resolver (Fork B rework, bd t7o3.11 / 3k10 / c8d8). The engine is
    // the single home of product config (the maintainer's Q2 choice): it resolves product_code → the
    // structural facts (term / variant / renewal policy / cadence / role) at constitution, so the
    // orchestrator carries NO product-family knowledge. Optional only so the existing direct callers
    // that already supply the full ConstituteDepositCommand (family unit tests, ADR-PC-024 paths)
    // keep working; the minimal saga path (ConstituteFromProductConfigAsync) requires it and fails
    // loud if it is absent.
    private readonly IProductConfigStore? _productConfigStore = productConfigStore;

    // The product's required commercial-eligibility preconditions (ADR-PC-024 §1, from the product
    // config's `required_preconditions`). Engine-instance config for the walking skeleton, mirroring
    // the pinned-pack / early-termination-policy stand-ins (ADR-PC-009): a per-deposit config registry
    // resolving it is later work. Defaults to an empty set — v1 launch products are NOT eligibility-
    // gated (02 §4), so the common path never refuses on preconditions.
    private readonly IReadOnlyCollection<string> _requiredPreconditions =
        requiredPreconditions ?? Array.Empty<string>();

    /// <summary>
    /// Constitute a deposit from the MINIMAL saga request (Fork B rework, bd t7o3.11 / 3k10 / c8d8):
    /// resolve the product code to its structural facts ENGINE-SIDE, then run the same
    /// resolve→decide→append constitution the full-command path runs. The saga sends only
    /// <c>{deposit_id, product_id, principal_cents, funding_account}</c>; the engine looks up the term
    /// / interest variant / renewal policy / coupon cadence / pricing role from its deployed
    /// product-config store, so the orchestrator carries no product-family knowledge.
    /// </summary>
    /// <remarks>
    /// <b>Step 0 — product-config resolve (engine-side, ADR-PC-009).</b> The structural facts are
    /// resolved from <see cref="IProductConfigStore"/> here, BEFORE the rate-sheet resolve, in the same
    /// service call. An unknown product code fails loud (<see cref="DomainRejectedException"/>), never a
    /// silent default — the engine is the fail-loud authority on whether a product code is known. The
    /// start date is derived from <see cref="MinimalConstituteDepositRequest.ConstitutedAt"/> (the
    /// engine is now the event author; the host stamps the instant from its clock). The resolved facts
    /// fill an internal <see cref="ConstituteDepositCommand"/> and the rest of the path is unchanged —
    /// the rate-sheet resolve + the decide + the single atomic append+outbox (ADR-PC-008 §S2).
    /// </remarks>
    /// <returns>The new stream's head version (ADR-IC-005 §P3 read-your-writes token / commit_sequence).</returns>
    public async Task<long> ConstituteFromProductConfigAsync(
        MinimalConstituteDepositRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var store = _productConfigStore
            ?? throw new InvalidOperationException(
                "No product-config store is configured for this engine instance; the minimal "
                + "constitution path resolves product_code → structural facts engine-side and cannot "
                + "run without it (Fork B rework, ADR-PC-009).");

        // Step 0: resolve the STRUCTURAL facts from the deployed product config — fail loud on an
        // unknown product code, exactly as an unpriced (product, role) fails the rate-sheet resolve.
        var config = store.Resolve(request.ProductId)
            ?? throw new DomainRejectedException(
                $"No product config found for '{request.ProductId}'; cannot constitute "
                + "(the engine resolves product_code → structural facts at constitution, ADR-PC-009).");

        // The engine is now the event author: derive the start date from the host-stamped instant
        // (ADR-PC-010 §P5 — the clock is the impure shell's; replay stability rides the Idempotency-Key
        // dedup, ADR-PC-029 slot 4). The role is the config's default unless the caller overrode it
        // (the orchestrator never does — the override is for direct callers).
        var command = new ConstituteDepositCommand(
            DepositId: request.DepositId,
            PrincipalCents: request.PrincipalCents,
            ProductId: request.ProductId,
            Role: request.Role ?? config.DefaultRole,
            TermDays: config.TermDays,
            StartDate: DateOnly.FromDateTime(request.ConstitutedAt.UtcDateTime),
            ConstitutedAt: request.ConstitutedAt,
            InterestVariant: config.InterestVariant,
            AutoRenewalPolicy: config.AutoRenewalPolicy,
            FundingAccount: request.FundingAccount,
            Actor: request.Actor,
            PaymentPeriodMonths: config.PaymentPeriodMonths,
            Preconditions: request.Preconditions,
            CommandId: request.CommandId);

        return await ConstituteAsync(command, ct);
    }

    /// <summary>
    /// Constitute a deposit from the FULL command: resolve the active rate sheet, stamp the TAN +
    /// version id, and append <c>DepositConstituted</c> as the stream's first event. The path is
    /// DE-SETTLED (bd babelstone-t7o3.4): the principal debit is NOT done here — it is the constitution
    /// saga's gated step (ReserveAccountBalance→ConfirmDebit against the Core ACL, ADR-PC-016 §68/§127 /
    /// ADR-PC-029 slot 2). The engine command DECIDES + APPENDS only; no money leg rides this path. For
    /// the ADVANCE variant it ALSO accrues + withholds the full-term interest at t=0 (02 §2.1
    /// <c>CF(0) = -C + J</c>) and appends the upfront <c>InterestPaid</c> triple alongside the
    /// constitution event in the same first transaction — the interest IS recognised in the engine's
    /// books at t=0, but its money leg is likewise the saga's gated credit, not an eager in-engine settle.
    /// This is the shared tail the minimal path (<see cref="ConstituteFromProductConfigAsync"/>) funnels
    /// into after resolving the structural facts engine-side.
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

        // 3. Resolve the product's F.12 partial-withdrawal policy from its product config and PIN it on
        //    the constitution event (bd k6r8.8/qze9): like the rate, the policy is fixed at constitution
        //    so a later config edit can never retroactively change a live deposit's withdrawal rights
        //    (ADR-PC-009 per-instance pinning). A product the store does not carry — or no store
        //    configured (direct callers) — pins the Unrestricted policy: no F.12 gates. Pure lookup,
        //    no clock/I-O in the pinned value.
        var partialWithdrawalPolicy = _productConfigStore?.Resolve(command.ProductId) is { } productConfig
            ? PartialWithdrawalPolicy.FromProductConfig(productConfig)
            : PartialWithdrawalPolicy.Unrestricted;

        // 4. Decide (pure): build the event, stamping the resolved TAN + the version it came from + the
        //    resolved partial-withdrawal policy.
        var constituted = TermDepositDecider.DecideConstitution(
            command, tan, resolution.RateSheetVersionId, partialWithdrawalPolicy);

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
    /// Withdraw part of a constituted deposit's principal before maturity (F.12; 02 §2.4.1, bd qze9):
    /// rehydrate it (must be Active — the F.3 gate decides), rebuild the product's partial-withdrawal
    /// policy from the gates PINNED on the deposit at constitution (bd k6r8.8/qze9), run the pure
    /// <see cref="PartialWithdrawalDecider"/>, and append the single <c>DepositPartiallyWithdrawn</c>
    /// event reducing the principal. UNLIKE early termination, a partial withdrawal CLOSES nothing and
    /// settles nothing — it is a principal reduction only (02 §2.4.1), so there is NO settlement leg here;
    /// the deposit stays Active. Withdrawing the whole balance is a termination (F.4), which the decider
    /// refuses. Triggered MANUALLY, as maturity is.
    /// </summary>
    /// <returns>The stream's head version after the withdrawal (ADR-IC-005 §P3 read-your-writes token / commit_sequence).</returns>
    public async Task<long> WithdrawPartiallyAsync(PartialWithdrawCommand command, CancellationToken ct = default)
    {
        // 1. Rehydrate the constituted position (load-then-append on the live stream head).
        var hydrated = await runtime.LoadAsync(command.DepositId, ct);
        var position = hydrated.State;

        // Transition-legality gate (F.3 state machine): a partial withdrawal is legal only from Active and
        // is STATE-PRESERVING (the deposit stays Active afterward). The single LifecycleTransitions table
        // decides, so a withdrawal on a Matured/closed (or not-yet-constituted) deposit is rejected
        // uniformly with every other illegal transition. (The pure decider re-checks this defensively.)
        RejectIfIllegal(
            position.Lifecycle, LifecycleTransitions.Transition.PartiallyWithdraw, command.DepositId, "withdraw partially");

        // 2. Rebuild the F.12 policy from the gates PINNED on the deposit at constitution (bd k6r8.8/qze9),
        //    NOT from the live product config — so the rules a deposit is subject to are the ones fixed
        //    when it was opened, immune to a later config edit (ADR-PC-009 per-instance pinning). 0/0/0
        //    (a pre-F.12 deposit, or a variant that omitted the block) is the Unrestricted policy.
        var policy = new PartialWithdrawalPolicy(
            position.MinWithdrawalCents, position.MinRemainingBalanceCents, position.LockupPeriodDays);

        // 3. Decide (pure): the withdrawal DATE is derived from the command instant and passed as an INPUT
        //    — no clock in the decider. A partial withdrawal carries NO money leg (02 §2.4.1), so unlike
        //    TerminateEarlyAsync there is no settlement.SettleAsync here; the decider returns the single
        //    DepositPartiallyWithdrawn that reduces the principal.
        var withdrawnOn = DateOnly.FromDateTime(command.WithdrawnAt.UtcDateTime);
        var events = PartialWithdrawalDecider.Decide(
            position, new Money(command.WithdrawnAmountCents), withdrawnOn, policy);

        // 4. Append at the current head (optimistic concurrency on the second append). The returned head
        //    version is the commit_sequence the caller threads for read-your-writes. Thread the CommandId
        //    so the append's in-transaction command_dedup INSERT fires (ADR-PC-029 slot 4): an at-least-once
        //    retry of the SAME withdrawal raises DuplicateCommandException and returns the original outcome,
        //    never a second append — UNLIKE the one-shot lifecycle steps, a partial withdrawal is repeatable
        //    (it leaves the deposit Active), so a non-idempotent retry would withdraw twice.
        return await runtime.AppendAsync(
            command.DepositId, hydrated.Version, events,
            Context(command.Actor, command.WithdrawnAt, command.CommandId), ct);
    }

    /// <summary>
    /// Record the GDPR Article 17 erasure fact on a deposit (bd babelstone-nzw6): append
    /// <see cref="PersonalDataErasureRequested"/> so the deposit folds to <c>Erased</c>. This method is
    /// the SECOND half of the right-to-be-forgotten flow — the host has ALREADY crypto-shredded the
    /// subject's key (<c>IPiiKeyStore.DestroyKeyAsync</c>, ADR-PC-004 §P3) at the OpenBao boundary
    /// before calling here, so this layer stays PII-free and only writes the structural audit fact.
    /// </summary>
    /// <remarks>
    /// Order matters and is the host's contract: the key is destroyed FIRST, then this event is appended.
    /// If the append fails after the key is gone, the (idempotent) destroy + a retried append converge —
    /// the key stays destroyed (erasure is irreversible by design) and the audit fact lands on retry.
    /// The lifecycle gate (F.3) makes erasing an already-Erased deposit illegal, which is also the
    /// idempotency guard against a double-erase request. The event carries only the structural facts the
    /// command supplies — never the raw subject id (ADR-PC-004 §P2).
    /// </remarks>
    public async Task<long> ErasePersonalDataAsync(ErasePersonalDataCommand command, CancellationToken ct = default)
    {
        // 1. Rehydrate the deposit (load-then-append on the live stream head).
        var hydrated = await runtime.LoadAsync(command.DepositId, ct);
        var position = hydrated.State;

        // 2. Transition-legality gate (F.3): erasure is legal from any state that still holds the
        //    subject's PII (live OR business-closed), never from Pending (no deposit) or Erased
        //    (already erased — the idempotency guard). The single LifecycleTransitions table decides.
        RejectIfIllegal(position.Lifecycle, LifecycleTransitions.Transition.Erase, command.DepositId, "erase personal data");

        // 3. Append the structural audit fact. No decider/financial math — erasure carries no money;
        //    the erasure DATE is derived from the command instant and passed as an input (no clock here).
        var erasedOn = DateOnly.FromDateTime(command.ErasedAt.UtcDateTime);
        var erased = new PersonalDataErasureRequested(
            InstanceId: command.DepositId,
            SubjectPseudonym: command.SubjectPseudonym,
            ErasedOn: erasedOn,
            ErasureReason: command.ErasureReason);

        // Thread the CommandId so the append's in-transaction command_dedup INSERT fires (ADR-PC-029
        // slot 4): an at-least-once retry of the SAME erasure raises DuplicateCommandException and
        // returns the original outcome, never a second append — irreversible key destruction demands it.
        return await runtime.AppendAsync(
            command.DepositId, hydrated.Version, [erased],
            Context(command.Actor, command.ErasedAt, command.CommandId), ct);
    }

    /// <summary>
    /// Open the renewed instance — step 2 of the renewal saga (bd babelstone-mtto PR B; steps 6–8 of
    /// the retired monolithic <c>RenewAsync</c>). Given the CLOSING deposit, which MUST already be
    /// <see cref="DepositLifecycle.Matured"/> (the autonomous maturity leg ran first), this:
    /// re-roots the new stream's causation at the closing <c>DepositMatured</c> event, resolves the
    /// renewal rate by the closing deposit's <c>auto_renewal_policy</c>, settles the rollover debit,
    /// and opens the NEW stream with <c>DepositConstituted</c> (plus the ADVANCE upfront-interest
    /// triple for an ADVANCE variant). The maturity CREDIT is NOT this command's leg —
    /// <see cref="MatureAsync"/> already credited it; only the <c>renewal_rollover</c> debit settles here.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Precondition is Matured, NOT Active</b> (the behavioural change from the monolith): the saga's
    /// ConstituteRenewal command fires AFTER <see cref="MatureAsync"/> has committed, so the closing
    /// stream head is already Matured. The F.3 <see cref="LifecycleTransitions"/> table has no
    /// Renew-from-Matured row (Renew is legal only from Active), so this asserts
    /// <c>Lifecycle == Matured</c> DIRECTLY rather than routing through the table — the same documented
    /// F.3 exception the monolith's step 9 carried (its rationale at the now-retired L488–499).
    /// </para>
    /// <para>
    /// <b>Idempotent per ADR-PC-029 slot 4.</b> The <c>constitute-renewal</c> endpoint takes a mandatory
    /// <c>Idempotency-Key</c> → <see cref="ConstituteRenewalCommand.CommandId"/>, pre-checks the command
    /// log, and the new-stream append's <c>command_dedup</c> raises <c>DuplicateCommandException</c>
    /// on a concurrent racer. The financial math is preserved BYTE-IDENTICAL to the monolith: the same
    /// pure deciders (<see cref="TermDepositDecider.ResolveRenewalRate"/>,
    /// <see cref="TermDepositDecider.DecideRenewalConstitution"/>, <see cref="TermDepositDecider.DecideAdvance"/>)
    /// decide the rate / new constitution / upfront interest — no money or rate math is re-derived here.
    /// </para>
    /// </remarks>
    /// <returns>The new stream's head version (ADR-IC-005 §P3 read-your-writes token / commit_sequence).</returns>
    public async Task<long> ConstituteRenewalAsync(ConstituteRenewalCommand command, CancellationToken ct = default)
    {
        // 1. Rehydrate the CLOSING deposit. It MUST already be Matured — the autonomous maturity leg
        //    (MatureAsync) precedes the saga, so by the time ConstituteRenewal fires the closing stream
        //    head is Matured. F.3 MODELLING DECISION (bd babelstone-mtto.3, RESOLVED): renewal is modelled
        //    as the single Active→Renewed business transition; the spec-mandated closing sequence
        //    (02 §2.4.4: DepositMatured THEN DepositRenewed, traversing Active→Matured→Renewed) is a
        //    deliberate saga SEQUENCING detail, NOT a second table transition — so the F.3 table keeps
        //    Renew Active-only and Matured stays a closed business-terminal state (the alternative —
        //    a Renew-from-Matured row — was rejected because it would breach the table's "every
        //    business-terminal state is closed to every business transition" invariant). This leg
        //    therefore asserts the Matured precondition DIRECTLY rather than through RejectIfIllegal,
        //    which also yields the richer, actionable domain message. Rejects a NOT-yet-matured (Active)
        //    or already-Renewed closing deposit before any sheet resolve or settlement.
        var hydrated = await runtime.LoadAsync(command.DepositId, ct);
        var closing = hydrated.State;
        if (closing.Lifecycle != DepositLifecycle.Matured)
        {
            throw new DomainRejectedException(
                $"Deposit {command.DepositId} is {closing.Lifecycle}; cannot constitute a renewal " +
                "(the closing deposit must already be Matured — the autonomous maturity leg precedes the " +
                "renewal saga; renewal is the single Active→Renewed transition, asserted directly, bd babelstone-mtto.3).");
        }

        // 2. The closing DepositMatured is the closing stream's head; its event id is the causation root
        //    for the new instance (02 §2.4.4 step 2). On a freshly-matured-not-yet-linked stream the head
        //    IS the DepositMatured, so LastEventId is exactly that event's id.
        var maturedEventId = hydrated.LastEventId;

        // 3. NONE never auto-renews: there is no new instance to constitute (02 §2.4.4). Reject loud —
        //    this is the new rejection path for ConstituteRenewal (the saga's header filter never starts a
        //    NONE-policy saga, but a direct caller is still rejected here, fail-loud not silent fall-through).
        if (closing.AutoRenewalPolicy == TermDepositDecider.RenewalNone)
        {
            throw new DomainRejectedException(
                $"Deposit {command.DepositId} has auto_renewal_policy NONE; it terminates at maturity, never renews.");
        }

        // 3a. PRE-MATURITY OPT-OUT WINDOW (the SAGA-START GATE, bd babelstone-mtto.3; 02 §2.4.4 /
        //     ADR-PC-023 §P). The depositor's final auto_renewal_optout_window_days before maturity is when
        //     a customer-initiated termination still blocks the renewal without penalty, so auto-renewal
        //     must NOT fire before that right has closed — i.e. not before the maturity date. This is the
        //     monolith's window-timing protection (removed in the mtto.2 decomposition, re-established here
        //     as the saga-start gate it now belongs at): a renewal triggered before maturity is rejected,
        //     the message distinguishing "within the N-day opt-out window" from "before maturity". The
        //     window length is the pack parameter, read FAIL-LOUD from PackParameters (ADR-PC-009 pinning).
        //     There is NO auto-firing maturity scheduler yet (DEF-2); when it lands it triggers the saga at/
        //     after maturity, so this gate is the standing protection in the meantime AND the belt-and-braces
        //     check once the scheduler exists. Pure: renewalDate / maturityDate are folded/command inputs,
        //     no clock read in this comparison (the instant was host-stamped at the boundary).
        var renewalDate = DateOnly.FromDateTime(command.RenewedAt.UtcDateTime);
        if (renewalDate < closing.MaturityDate)
        {
            var optOutWindowOpens = closing.MaturityDate.AddDays(-pack.Parameters.AutoRenewalOptoutWindowDays);
            var reason = renewalDate >= optOutWindowOpens
                ? $"within the {pack.Parameters.AutoRenewalOptoutWindowDays}-day pre-maturity opt-out window"
                : "before maturity";
            throw new DomainRejectedException(
                $"Deposit {command.DepositId} cannot auto-renew on {renewalDate:O} ({reason}); the opt-out " +
                $"window closes at maturity {closing.MaturityDate:O} and renewal fires no earlier.");
        }

        // 4. Resolve EVERY renewal fact from the CLOSING deposit, NOT the command (bd babelstone-mtto.5).
        //    The closing deposit now persists role + funding alongside the already-persisted product code,
        //    so the engine recovers product / role / funding from the folded state it just loaded —
        //    keeping product-family knowledge out of the orchestrator (ADR-IC-003 §A7). The product code
        //    is closing.ProductCode; the EFFECTIVE role applies the pre-field-deposit fallback (empty →
        //    standard, the v1 default) so a renewal of a deposit constituted before role was persisted
        //    still reprices; the funding token is closing.FundingAccount.
        var productCode = closing.ProductCode;
        var renewalRole = TermDepositDecider.EffectiveRenewalRole(closing);

        // Fail LOUD on an empty funding token (a deposit constituted before funding_account was
        // persisted): the renewal_rollover debit cannot target an empty/unknown funding reference, and
        // the engine never invents one — the same fail-loud discipline as an unpriced (product, role) or
        // an unknown product code. Checked before any sheet resolve or settlement.
        var fundingAccount = closing.FundingAccount;
        if (string.IsNullOrEmpty(fundingAccount))
        {
            throw new DomainRejectedException(
                $"Deposit {command.DepositId} carries no funding_account (constituted before the field was "
                + "persisted, bd babelstone-mtto.5); cannot constitute a renewal — the renewal_rollover debit "
                + "has no funding reference to target and the engine never invents one.");
        }

        // 5. Pack-resolved primitives (fail loud). Resolve the renewal rate by policy — EXACTLY the
        //    monolith's step-6 logic: SAME_TERM_CURRENT_RATE re-resolves the sheet at the renewal moment
        //    (the bank's then-current standard rate) against the CLOSING deposit's (product, role);
        //    SAME_TERM_SAME_RATE carries the original rate (the pure decider picks). Re-resolution is
        //    fail-loud, exactly as constitution. The same renewalRole feeds the re-resolution AND the
        //    renewed event's stamped role, so the new instance is priced and recorded against one role.
        var (dayCount, withholdingBps) = ResolvePrimitives();
        // renewalDate was derived above for the opt-out-window gate (step 3a); reused here for the rate resolve.
        int renewalTan;
        string renewalRateSheetVersionId;
        if (closing.AutoRenewalPolicy == TermDepositDecider.RenewalSameTermCurrentRate)
        {
            var resolution = await rateSheets.ResolveAsync(Family.FamilyName, command.RenewedAt, ct)
                ?? throw new DomainRejectedException(
                    $"No rate sheet effective for '{Family.FamilyName}' at {command.RenewedAt:O} to renew {command.DepositId}.");
            var currentTan = resolution.ResolveTanBasisPoints(productCode, renewalRole, closing.RemainingPrincipal.Cents)
                ?? throw new DomainRejectedException(
                    $"Rate sheet '{resolution.RateSheetVersionId}' does not price " +
                    $"({productCode}, {renewalRole}) at {closing.RemainingPrincipal.Cents}c to renew {command.DepositId}.");
            (renewalTan, renewalRateSheetVersionId) =
                TermDepositDecider.ResolveRenewalRate(closing, currentTan, resolution.RateSheetVersionId);
        }
        else
        {
            // SAME_TERM_SAME_RATE: no re-resolution — the decider carries the closing rate forward.
            (renewalTan, renewalRateSheetVersionId) =
                TermDepositDecider.ResolveRenewalRate(closing, closing.TanBasisPoints, closing.RateSheetVersionId);
        }

        // 6. Decide (pure): the new constitution from the rolled-over principal at the policy-resolved
        //    rate, for the same term/variant/cadence/policy as the closing deposit (monolith step 7),
        //    carrying the closing deposit's product / role / funding forward onto the renewed event
        //    (chain preservation across renewal generations, bd babelstone-mtto.5).
        var renewed = TermDepositDecider.DecideRenewalConstitution(
            closing, command.NewDepositId, closing.RemainingPrincipal, renewalTan, renewalRateSheetVersionId,
            renewalDate, renewalRole, fundingAccount);

        // 7. Settle the rolled-over principal into the new instance (the renewal_rollover debit, monolith
        //    step 8a) against the CLOSING deposit's funding token. The maturity credit already moved out in
        //    MatureAsync; only the rollover debit settles here, so the settlement legs now SPLIT across two
        //    calls — maturity credit from MatureAsync, rollover debit from here. ADVANCE pays its full-term
        //    interest up front (monolith step 8b), the same as a fresh constitution; the upfront
        //    InterestPaid rides in the new stream's first transaction.
        await settlement.SettleAsync(
            new SettlementInstruction(
                command.NewDepositId, SettlementDirection.Debit, renewed.Principal,
                fundingAccount, "renewal_rollover"),
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
                    fundingAccount, "advance_interest"),
                ct);
            renewalConstitutionEvents.AddRange(advance);
        }

        // 8. Open the new stream (expectedVersion -1) — its causation_id roots at the closing
        //    DepositMatured (02 §2.4.4 step 2). The CommandId makes this append idempotent: a replay
        //    returns the new stream's head with no second append (ADR-PC-029 slot 4).
        return await runtime.AppendAsync(
            command.NewDepositId, expectedVersion: -1, renewalConstitutionEvents,
            Context(command.Actor, command.RenewedAt, command.CommandId) with { CausationId = maturedEventId }, ct);
    }

    /// <summary>
    /// Link the renewal — step 3 of the renewal saga (bd babelstone-mtto PR B; step 9 of the retired
    /// monolithic <c>RenewAsync</c>): append <c>DepositRenewed</c> to the CLOSING stream, folding it
    /// from Matured to Renewed (terminal) — the old→new lookup the maturity calendar uses. Loads the
    /// closing (Matured) deposit and the new deposit (folding its head <c>DepositConstituted</c> for the
    /// renewed facts), calls the pure <see cref="TermDepositDecider.DecideRenewalLink"/>, and appends at
    /// the closing stream's post-maturity head.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>No F.3 re-gate</b> (mirroring the monolith's documented exception): the closing stream head is
    /// Matured, and the F.3 table models Matured as a closed business-terminal state — Renew is legal only
    /// from Active (terminality is expressed as absence from every business-transition source set). The
    /// spec-mandated closing sequence (02 §2.4.4: DepositMatured THEN DepositRenewed) thus legally traverses
    /// Active→Matured→Renewed. <b>F.3 MODELLING DECISION (bd babelstone-mtto.3, RESOLVED):</b> renewal is
    /// modelled as the single Active→Renewed business transition; the Matured→Renewed step is a deliberate
    /// saga SEQUENCING detail, NOT a second table transition — so we do NOT add a Renew-from-Matured row
    /// (which would breach the table's "every business-terminal state is closed to every business
    /// transition" invariant — see <c>LifecycleTransitionsTests</c>). Renew legality is established at the
    /// saga-start precondition (only non-NONE-policy deposits ever start a renewal saga, and not before the
    /// opt-out window closes) and the Matured-precondition assertion in <see cref="ConstituteRenewalAsync"/>.
    /// </para>
    /// <para>
    /// <b>Idempotent per ADR-PC-029 slot 4</b> — the <c>renewal-link</c> endpoint threads the mandatory
    /// <c>Idempotency-Key</c> → <see cref="LinkRenewalCommand.CommandId"/> onto the closing-stream append.
    /// </para>
    /// </remarks>
    /// <returns>The closing stream's head version after the link (ADR-IC-005 §P3 read-your-writes token).</returns>
    public async Task<long> LinkRenewalAsync(LinkRenewalCommand command, CancellationToken ct = default)
    {
        // 1. Rehydrate the CLOSING deposit — it MUST be Matured (ConstituteRenewal opened the new stream
        //    off this Matured head). Same direct Matured assertion as ConstituteRenewalAsync, for the same
        //    F.3 reason (no Renew-from-Matured row): a not-yet-matured or already-Renewed closing deposit
        //    is rejected before any append.
        var closingHydrated = await runtime.LoadAsync(command.DepositId, ct);
        var closing = closingHydrated.State;
        if (closing.Lifecycle != DepositLifecycle.Matured)
        {
            throw new DomainRejectedException(
                $"Deposit {command.DepositId} is {closing.Lifecycle}; cannot link a renewal " +
                "(the closing deposit must be Matured — the renewal-link step folds Matured → Renewed).");
        }

        // 2. Reconstruct the renewed instance's constitution from the NEW stream's head DepositConstituted,
        //    so DecideRenewalLink reads the new instance's pinned facts (rate / version / term / dates)
        //    exactly as the monolith did from the in-call `renewed` event. The new stream's first event
        //    IS the DepositConstituted ConstituteRenewalAsync appended.
        var newHydrated = await runtime.LoadAsync(command.NewDepositId, ct);
        if (newHydrated.Version < 0)
        {
            throw new DomainRejectedException(
                $"Renewed deposit {command.NewDepositId} does not exist; cannot link the renewal " +
                "(ConstituteRenewal must open the new stream before the link step).");
        }
        var renewed = ConstitutedFromPosition(newHydrated.State);

        // 3. Decide (pure) and append the DepositRenewed link, folding the closing deposit to Renewed
        //    (terminal). Appended at the closing stream's post-maturity head with optimistic concurrency.
        //    The CommandId makes this append idempotent (ADR-PC-029 slot 4).
        var link = TermDepositDecider.DecideRenewalLink(closing, renewed);
        return await runtime.AppendAsync(
            command.DepositId, closingHydrated.Version, [link],
            Context(command.Actor, command.RenewedAt, command.CommandId), ct);
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
            ProductCode = constituted.ProductCode,
            Role = constituted.Role,
            FundingAccount = constituted.FundingAccount,
            MinWithdrawalCents = constituted.MinWithdrawalCents,
            MinRemainingBalanceCents = constituted.MinRemainingBalanceCents,
            LockupPeriodDays = constituted.LockupPeriodDays,
            RemainingPrincipal = constituted.Principal,
            // Seed the opening principal segment, mirroring DepositConstitutedHandler, so this head
            // projection stays a faithful stand-in for the real fold (bd babelstone-emtr) — even though
            // the only current caller (ADVANCE) reads Principal, not the timeline.
            PrincipalTimeline = [new PrincipalSegment(constituted.StartDate, constituted.Principal)],
            Lifecycle = DepositLifecycle.Active,
        };

    /// <summary>
    /// Reconstruct the renewed instance's head <see cref="DepositConstituted"/> from the NEW stream's
    /// folded position, so <see cref="LinkRenewalAsync"/> hands <see cref="TermDepositDecider.DecideRenewalLink"/>
    /// the SAME pinned facts the monolith passed it from the in-call <c>renewed</c> event. Pure projection
    /// off the position the new stream's <c>DepositConstituted</c> folded: <c>DecideRenewalLink</c> reads only
    /// the rate / version / term / start / maturity / principal, all carried verbatim on the position
    /// (RemainingPrincipal equals Principal for a fresh, un-withdrawn constitution). No clock, no I/O.
    /// </summary>
    private static DepositConstituted ConstitutedFromPosition(DepositPosition position) =>
        new(
            DepositId: position.DepositId,
            Principal: position.Principal,
            TanBasisPoints: position.TanBasisPoints,
            RateSheetVersionId: position.RateSheetVersionId,
            TermDays: position.TermDays,
            StartDate: position.StartDate,
            MaturityDate: position.MaturityDate,
            InterestVariant: position.InterestVariant,
            AutoRenewalPolicy: position.AutoRenewalPolicy,
            PaymentPeriodMonths: position.PaymentPeriodMonths,
            ProductCode: position.ProductCode,
            Role: position.Role,
            FundingAccount: position.FundingAccount,
            MinWithdrawalCents: position.MinWithdrawalCents,
            MinRemainingBalanceCents: position.MinRemainingBalanceCents,
            LockupPeriodDays: position.LockupPeriodDays);

    // commandId is the OPTIONAL command-ingress idempotency key (ADR-PC-029 slot 4): the
    // constitution paths thread the command's CommandId so the append dedupes on it; the
    // engine-internal lifecycle steps (maturity, coupon, termination, renewal) pass none.
    private AppendContext Context(string actor, DateTimeOffset validTime, Guid? commandId = null) =>
        new(Family.FamilyName, pack.VersionKey, Family.SchemaVersion, actor, validTime, CommandId: commandId);
}
