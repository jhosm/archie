using Babelstone.Engine;
using Babelstone.FinancialMath;
using Babelstone.FinancialTypes;

namespace Babelstone.Families.TermDeposit.Application;

/// <summary>
/// The pure decision core of the term-deposit decider (ADR-PC-021 §P3): given a command plus
/// the inputs the service resolved (the rate-sheet TAN, the pack day-count and withholding),
/// it produces the events — running the financial-math kernel command-side, never in a fold.
/// No clock, no I/O, no randomness: every time/value input is explicit, so this is unit-tested
/// Docker-free. The impure orchestration (resolve, settle, append) lives in
/// <see cref="TermDepositConstitutionService"/>; keeping the two apart is what lets the shared
/// choreography lift into a generic pipeline later (ADR-PC-021 §P5, bd babelstone-osv6).
/// </summary>
public static class TermDepositDecider
{
    /// <summary>The interest-variant discriminators (02 §2.1) the decider branches on — the same
    /// tokens the CUE family schema enumerates and the command/event carry.</summary>
    public const string AtMaturity = "AT_MATURITY";
    public const string Periodic = "PERIODIC";
    public const string Advance = "ADVANCE";

    /// <summary>The auto-renewal policy discriminators (02 §2.4.4) the renewal branches on — the same
    /// tokens the CUE family schema enumerates and <c>DepositConstituted.AutoRenewalPolicy</c> carries.</summary>
    public const string RenewalNone = "NONE";
    public const string RenewalSameTermCurrentRate = "SAME_TERM_CURRENT_RATE";
    public const string RenewalSameTermSameRate = "SAME_TERM_SAME_RATE";

    /// <summary>The v1 default pricing role. For EVERY v1 launch product the engine's product-config
    /// store resolves <c>ProductConfig.DefaultRole == "standard"</c>, so this constant equals that
    /// config default for the only deposits that can exist in v1. A renewal of a deposit constituted
    /// BEFORE the per-deposit <c>role</c> was persisted (bd babelstone-mtto.5) carries an empty closing
    /// role; the renewal falls back to this default so the <c>(product, role)</c> re-resolution still
    /// works rather than failing on an empty role (see <see cref="EffectiveRenewalRole"/>). NOTE: should
    /// a future product carry a non-<c>standard</c> default role, this hardcoded fallback would diverge
    /// from that config default for a pre-mtto.5 deposit of that product — a path that cannot occur in v1
    /// (no such product, and no pre-mtto.5 deposit of one), flagged for re-resolution from the product
    /// config if a per-deposit role-selector lands.</summary>
    public const string DefaultRole = "standard";

    /// <summary>
    /// The effective pricing role for a renewal: the closing deposit's persisted role, or
    /// <see cref="DefaultRole"/> when it is empty — the pre-field-deposit fallback (bd babelstone-mtto.5).
    /// A deposit constituted before <c>role</c> was persisted folds to <c>Role == ""</c>; defaulting it
    /// to <c>standard</c> (the v1 default role) keeps the renewal's <c>(product, role)</c> rate
    /// re-resolution working rather than rejecting on an empty role. Pure — no clock, no I/O. The SAME
    /// effective role feeds both the rate re-resolution and the renewed event's stamped role, so the
    /// renewed instance is priced and recorded against one role (chain preservation).
    /// </summary>
    public static string EffectiveRenewalRole(DepositPosition closing) =>
        string.IsNullOrEmpty(closing.Role) ? DefaultRole : closing.Role;

    /// <summary>The closed, engine-owned commercial-eligibility verdict-key taxonomy (ADR-PC-024 §1, §6) —
    /// the SAME tokens the CUE family schema's <c>#PreconditionKey</c> enumerates and a product's
    /// <c>required_preconditions</c> picks from. The engine owns this set and the refusal semantics;
    /// the product config owns WHICH keys it requires; upstream owns evaluating them.</summary>
    public const string PreconditionIsNewClient = "is_new_client";
    public const string PreconditionIsNewMoney = "is_new_money";
    public const string PreconditionSalaryDomiciled = "salary_domiciled";
    public const string PreconditionMortgageLinked = "mortgage_linked";

    /// <summary>The <see cref="DepositConstitutionFailed.FailureReason"/> code a precondition refusal
    /// records (ADR-PC-024 §5). Stable, machine-readable, non-PII — like every failure reason.</summary>
    public const string EligibilityNotMetReason = "ELIGIBILITY_NOT_MET";

    /// <summary>
    /// Build <see cref="DepositConstituted"/> from the command, stamping the resolved TAN and
    /// the rate-sheet version it came from (ADR-PC-008 §P3). The maturity date is derived from
    /// the start date and term — an explicit field on the event, not recomputed downstream. The
    /// catalogue <c>ProductCode</c> is stamped from the already-available <c>command.ProductId</c>
    /// (the structural product identifier the rate sheet priced the TAN against) so the D.4 read
    /// model can denormalize it — no new command input (bd babelstone-v794). The pricing
    /// <c>Role</c> and the opaque <c>FundingAccount</c> token are likewise stamped from the command
    /// (both already command inputs) so a later auto-renewal can recover ALL renewal facts (product
    /// / role / funding) from the closing deposit alone, keeping product/funding knowledge out of
    /// the orchestrator (bd babelstone-mtto.5).
    /// </summary>
    public static DepositConstituted DecideConstitution(
        ConstituteDepositCommand command, int tanBasisPoints, string rateSheetVersionId,
        PartialWithdrawalPolicy partialWithdrawalPolicy) =>
        new(
            DepositId: command.DepositId,
            Principal: new Money(command.PrincipalCents),
            TanBasisPoints: tanBasisPoints,
            RateSheetVersionId: rateSheetVersionId,
            TermDays: command.TermDays,
            StartDate: command.StartDate,
            MaturityDate: command.StartDate.AddDays(command.TermDays),
            InterestVariant: command.InterestVariant,
            AutoRenewalPolicy: command.AutoRenewalPolicy,
            PaymentPeriodMonths: command.PaymentPeriodMonths,
            // The resolved commercial-eligibility verdicts are NOT recorded on this ACCEPTED-path event
            // in v1 (ADR-PC-024 §1 Amendment 2026-06-12): DepositConstituted is bus-published and the Avro
            // codec enforces strict parity with no array-of-record support, so an accepted-path verdict
            // list would force store-only audit lineage onto the durable bus. The REFUSAL-path lineage
            // (the load-bearing CONSTITUTION_PRECONDITION_REFUSAL commitment) rides DepositConstitutionFailed
            // (store-only JSON, ADR-PC-028); accepted-path on-envelope lineage is deferred to v1.x.
            ProductCode: command.ProductId,
            // The pricing role + opaque funding-account token the rate sheet priced / the principal
            // was debited (bd babelstone-mtto.5). Persisted on the event (and folded onto the
            // position) so a later auto-renewal recovers ALL renewal facts — product / role / funding
            // — from the closing deposit it already loads, never from the renewal command.
            Role: command.Role,
            FundingAccount: command.FundingAccount,
            // The F.12 partial-withdrawal policy resolved from the product config and PINNED here
            // (bd k6r8.8/qze9): stamped at constitution exactly as the rate is, so the gates a live
            // deposit is subject to are fixed for its life (ADR-PC-009). The impure service resolves
            // the policy from the product config and passes it in; the decider stays pure.
            MinWithdrawalCents: partialWithdrawalPolicy.MinWithdrawalCents,
            MinRemainingBalanceCents: partialWithdrawalPolicy.MinRemainingBalanceCents,
            CarenciaDays: partialWithdrawalPolicy.CarenciaDays);

    /// <summary>
    /// Decide commercial eligibility (ADR-PC-024 §5): refuse the constitution when a precondition the
    /// product REQUIRES is absent from the command's verdicts or evaluated <c>Satisfied == false</c>.
    /// Pure function of <paramref name="requiredPreconditions"/> (the product's declared gate) and
    /// <paramref name="verdicts"/> (the verdicts the saga resolved upstream and placed on the command)
    /// — NO upstream call, no in-engine evaluation, no clock (ADR-PC-024 §3–§4). The engine never
    /// re-evaluates a verdict; it only checks that each required key is present and satisfied.
    /// <list type="bullet">
    /// <item>All required preconditions present and <c>Satisfied</c> ⇒ returns <c>null</c> (constitute proceeds).</item>
    /// <item>Any required key absent, or present but <c>Satisfied == false</c> ⇒ returns a
    /// <see cref="DepositConstitutionFailed"/> with reason <see cref="EligibilityNotMetReason"/> and a
    /// non-PII detail naming the unmet keys. No deposit is opened; this is a REFUSAL, not a compensation —
    /// the engine refuses BEFORE the irreversible Core debit (ADR-PC-024 §5).</item>
    /// </list>
    /// Replay re-presents the same command verdicts and re-derives the identical outcome — the refusal is
    /// idempotent because no upstream re-call ever happens inside the engine (ADR-PC-024 §4). A product with
    /// no <paramref name="requiredPreconditions"/> (v1 launch products, 02 §4) is never refused here.
    /// </summary>
    /// <param name="depositId">The stream id the (would-be) deposit and any refusal event are keyed by.</param>
    /// <param name="requiredPreconditions">The closed verdict keys this product's config requires
    /// (ADR-PC-024 §1, from <c>required_preconditions</c>). Empty ⇒ ungated ⇒ never refused.</param>
    /// <param name="verdicts">The resolved verdicts the saga placed on the command, keyed by verdict key.</param>
    /// <returns><c>null</c> when every required precondition is satisfied; otherwise the refusal event.</returns>
    public static DepositConstitutionFailed? CheckPreconditions(
        Guid depositId,
        IReadOnlyCollection<string> requiredPreconditions,
        IReadOnlyDictionary<string, PreconditionVerdict>? verdicts)
    {
        // Ungated products (the v1 launch set) never reach a refusal — fast path, no allocation.
        if (requiredPreconditions.Count == 0)
        {
            return null;
        }

        // A required key fails when it is absent from the command's verdicts OR its verdict is not
        // satisfied. Order the keys deterministically so the detail string (and thus the recorded
        // event) is identical on replay — purity extends to the failure message, not just the verdict.
        var unmet = requiredPreconditions
            .Where(key => verdicts is null
                || !verdicts.TryGetValue(key, out var verdict)
                || !verdict.Satisfied)
            .OrderBy(key => key, StringComparer.Ordinal)
            .ToArray();

        if (unmet.Length == 0)
        {
            return null;
        }

        // Detail names the unmet KEYS only — never the verdict's evidence_ref or any customer fact
        // (ADR-PC-024 §1, §5; the keys are the engine-owned closed taxonomy, structural not PII). The
        // full resolved verdicts are recorded on the event for AUDIT LINEAGE (ADR-PC-024 §1): which
        // verdict drove the refusal and on what referenced evidence, beyond the unmet-key names.
        return new DepositConstitutionFailed(
            depositId,
            EligibilityNotMetReason,
            $"Required commercial-eligibility precondition(s) absent or not satisfied: {string.Join(", ", unmet)}.",
            RecordedVerdicts(verdicts));
    }

    /// <summary>
    /// Map the command's resolved verdicts into the structural <see cref="RecordedPreconditionVerdict"/>
    /// lineage recorded on the constitution event (ADR-PC-024 §1 "for audit lineage only"). Ordered by
    /// key (Ordinal) so the recorded list is REPLAY-IDENTICAL regardless of the command map's iteration
    /// order — purity extends to the lineage artefact, not just the verdict. Pure: no clock, no I/O;
    /// the <c>evaluated_at</c> is upstream-supplied data carried through, never a clock read. Empty/absent
    /// verdicts map to an empty list (ungated deposits and pre-F.9 streams carry none).
    /// </summary>
    private static IReadOnlyList<RecordedPreconditionVerdict> RecordedVerdicts(
        IReadOnlyDictionary<string, PreconditionVerdict>? verdicts) =>
        verdicts is null || verdicts.Count == 0
            ? Array.Empty<RecordedPreconditionVerdict>()
            : verdicts
                .OrderBy(kv => kv.Key, StringComparer.Ordinal)
                .Select(kv => new RecordedPreconditionVerdict(
                    kv.Key, kv.Value.Satisfied, kv.Value.EvidenceRef, kv.Value.EvaluatedAt))
                .ToArray();

    /// <summary>
    /// The resolved rate VECTOR a deposit accrues against (F.10): a step-up (<i>crescente</i>) or
    /// amount-tiered (<i>escalonada</i>) <see cref="RateSchedule"/> when the caller resolved one at
    /// constitution, or the degenerate FLAT schedule built from <paramref name="position"/>'s single
    /// <see cref="DepositPosition.TanBasisPoints"/> otherwise. Centralising the "schedule-or-flat"
    /// choice here keeps every accrual path (maturity, coupon, advance, early termination) folding
    /// over ONE primitive — and a null schedule reproduces the pre-F.10 flat math byte-for-byte (a
    /// one-segment flat vector equals <see cref="Accrual.SimpleInterest"/> to the cent), so the
    /// vector is purely additive and the common flat product is unchanged. Pure: no clock, no I/O.
    /// </summary>
    private static RateSchedule ScheduleOrFlat(DepositPosition position, RateSchedule? schedule) =>
        schedule ?? RateSchedule.Flat(position.TanBasisPoints);

    /// <summary>
    /// Mature a deposit, branching on its interest variant (02 §2.1). Pure: the position carries
    /// every input, the pack supplies the convention and rate.
    /// <list type="bullet">
    /// <item><b>AT_MATURITY</b> — the single flow: accrue gross interest over the full term, withhold
    /// once, pay out principal + net (<c>CF(maturity) = +C + J</c>).</item>
    /// <item><b>PERIODIC</b> — the FINAL coupon: accrue interest for the last coupon window only
    /// (last-paid-through → maturity), withhold that one flow, and pay out principal + that final net
    /// (<c>CF(n) = +C + J_n</c>). The intermediate coupons were each paid by
    /// <see cref="DecideInterestPayment"/>; maturity must NOT re-accrue the whole term.</item>
    /// <item><b>ADVANCE</b> — principal ONLY: interest for the full term was paid at t=0 by
    /// <see cref="DecideAdvance"/>, so maturity emits a zero-interest <see cref="DepositMatured"/>
    /// returning the principal alone (<c>CF(n) = +C</c>). No re-accrual.</item>
    /// </list>
    /// Withholding is always flow-by-flow (one <see cref="Withholding.Withhold"/> per flow), never
    /// rate-scaled and never applied to an aggregate (fin-math §5.4).
    /// </summary>
    /// <param name="schedule">The resolved rate VECTOR (F.10) the accrual folds over — a step-up or
    /// amount-tiered <see cref="RateSchedule"/> when one was resolved at constitution, or
    /// <c>null</c> for the flat single-TAN product (the pre-F.10 default, folded as a degenerate
    /// flat schedule, byte-identical math). ADVANCE re-accrues nothing at maturity, so the schedule
    /// does not reach its branch.</param>
    public static IReadOnlyList<DomainEvent> DecideMaturity(
        DepositPosition position, DayCountConvention dayCount, int withholdingBasisPoints,
        RateSchedule? schedule = null) =>
        position.InterestVariant switch
        {
            Advance => MatureAdvance(position),
            Periodic => MatureFinalCoupon(position, dayCount, withholdingBasisPoints, ScheduleOrFlat(position, schedule)),
            // AT_MATURITY is the default — the single-flow full-term accrual.
            _ => MatureSingleFlow(position, position.StartDate, position.MaturityDate, dayCount, withholdingBasisPoints,
                ScheduleOrFlat(position, schedule)),
        };

    /// <summary>
    /// Pay one intermediate PERIODIC coupon (02 §2.1 <c>CF(k) = +J_k</c>, k = 1..n-1): accrue the
    /// coupon window's interest on the resolved day-count, withhold that ONE flow, and emit
    /// <see cref="InterestPaid"/>. Principal is untouched (periodic deposits do NOT compound the
    /// balance — coupons are paid OUT to the current account). Withholding is per-coupon, so the
    /// realized net is the sum of each coupon's net, NEVER <c>gross_total × (1 − rate)</c> on the
    /// aggregate (fin-math §5.4 — the rate-scaling shortcut is exact only for a single flow).
    /// Pure: the window dates and pack rate/convention are explicit inputs.
    /// </summary>
    /// <param name="periodStart">The coupon window's inclusive start (the previous coupon's end,
    /// or the deposit start for the first coupon).</param>
    /// <param name="periodEnd">The coupon window's exclusive end (the coupon's due/paid date).</param>
    /// <remarks>
    /// Emits ONLY <see cref="InterestPaid"/> — a self-contained coupon event that carries gross, tax,
    /// AND net, and whose <see cref="InterestPaidHandler"/> folds all three running tallies
    /// (AccruedGrossInterest, WithholdingToDate, NetInterest). It deliberately does NOT also emit the
    /// AT_MATURITY <see cref="InterestAccrued"/> + <see cref="WithholdingApplied"/> pair: those
    /// handlers accumulate the SAME tallies, so emitting both alongside InterestPaid would
    /// double-count every coupon. The AT_MATURITY single flow uses the Accrued+Withheld pair (it has
    /// no InterestPaid); the coupon flow uses InterestPaid. One accumulation path per flow.
    /// </remarks>
    /// <param name="schedule">The resolved rate VECTOR (F.10) this coupon window accrues over — a
    /// step-up <see cref="RateSchedule"/> (a quarterly coupon on a <i>crescente</i> deposit accrues
    /// at the rate in force over ITS window, so a later coupon earns more) or <c>null</c> for the
    /// flat product. AccrueGross over [periodStart, periodEnd] folds only the segments overlapping
    /// the window, so the coupon prices the elapsed-day band it falls in.</param>
    public static IReadOnlyList<DomainEvent> DecideInterestPayment(
        DepositPosition position, DateOnly periodStart, DateOnly periodEnd,
        DayCountConvention dayCount, int withholdingBasisPoints, RateSchedule? schedule = null)
    {
        // The schedule is anchored at the deposit START, but a coupon window opens partway through
        // the term. AccrueGrossWindow attributes each segment's days to the [periodStart, periodEnd]
        // window relative to that anchor and rounds ONCE — so a window that straddles a step
        // boundary is priced segment-by-segment, and a flat schedule reduces exactly to
        // SimpleInterest over the window (the pre-F.10 coupon math, byte-identical).
        var rates = ScheduleOrFlat(position, schedule);
        // Accrue over the PRINCIPAL TIMELINE (F.12, bd babelstone-emtr), not a single principal: a
        // coupon window that opens after a partial withdrawal accrues on the reduced balance, and one
        // that straddles a withdrawal is split exactly at the withdrawal date. A deposit that never
        // withdrew has a single-segment timeline, so this is byte-identical to the prior single-principal
        // accrual (the no-regression equivalence the flat/timeline paths both guarantee).
        var gross = rates.AccrueGrossWindowOverPrincipal(
            position.PrincipalTimeline, position.StartDate, periodStart, periodEnd, dayCount);
        var withheld = Withholding.Withhold(gross, withholdingBasisPoints);

        return [new InterestPaid(position.DepositId, withheld.Gross, withheld.Tax, withheld.Net, periodEnd)];
    }

    /// <summary>
    /// Pay ADVANCE interest up front at constitution (02 §2.1 <c>CF(0) = -C + J</c>): the FULL-term
    /// nominal interest is the same <see cref="Accrual.SimpleInterest"/> over (start → maturity) as
    /// AT_MATURITY — there is NO present-value discounting; ADVANCE is a pure timing/presentation
    /// difference (fin-math §5.3). Withhold once at t=0 and emit <see cref="InterestPaid"/> dated the
    /// start. The principal alone returns at maturity (<see cref="MatureAdvance"/>). Pure: the start,
    /// maturity, and pack rate/convention are explicit inputs.
    /// </summary>
    /// <remarks>
    /// Like <see cref="DecideInterestPayment"/>, emits ONLY <see cref="InterestPaid"/> (dated the
    /// start) — the self-contained payout event that folds gross/tax/net once. It does NOT also emit
    /// <see cref="InterestAccrued"/> + <see cref="WithholdingApplied"/>, which would double-count the
    /// same tallies.
    /// </remarks>
    /// <param name="schedule">The resolved rate VECTOR (F.10) the full-term interest folds over —
    /// a step-up/amount-tiered <see cref="RateSchedule"/> resolved at constitution, or <c>null</c>
    /// for the flat product. The whole-term gross is one flow at t=0 regardless of the vector
    /// shape; the schedule only changes how that single gross figure is computed.</param>
    public static IReadOnlyList<DomainEvent> DecideAdvance(
        DepositPosition position, DayCountConvention dayCount, int withholdingBasisPoints,
        RateSchedule? schedule = null)
    {
        var gross = ScheduleOrFlat(position, schedule)
            .AccrueGross(position.Principal, position.StartDate, position.MaturityDate, dayCount);
        var withheld = Withholding.Withhold(gross, withholdingBasisPoints);

        return [new InterestPaid(position.DepositId, withheld.Gross, withheld.Tax, withheld.Net, position.StartDate)];
    }

    // ---- variant-specific maturity flows -------------------------------------------------------

    /// <summary>AT_MATURITY: one accrual over the whole term (folded over the resolved rate vector),
    /// one withholding, payout = principal + net. The stepped/tiered vector folds into the SINGLE
    /// maturity flow — still ONE InterestAccrued, so no contract change (F.10).</summary>
    private static IReadOnlyList<DomainEvent> MatureSingleFlow(
        DepositPosition position, DateOnly start, DateOnly end,
        DayCountConvention dayCount, int withholdingBasisPoints, RateSchedule schedule)
    {
        // Accrue over the principal TIMELINE and return the principal still ON DEPOSIT (F.12, bd
        // babelstone-emtr): a deposit that partially withdrew mid-term accrues each sub-period on the
        // principal actually held and returns the reduced principal — never the original (which would
        // double-pay the withdrawn part). With no withdrawal the timeline is one segment and this is
        // byte-identical to the prior single-principal maturity.
        var gross = schedule.AccrueGrossWindowOverPrincipal(
            position.PrincipalTimeline, position.StartDate, start, end, dayCount);
        var withheld = Withholding.Withhold(gross, withholdingBasisPoints);
        var payout = position.RemainingPrincipal + withheld.Net;

        return
        [
            new InterestAccrued(gross, end),
            new WithholdingApplied(withheld.Tax, withheld.Net),
            new DepositMatured(position.RemainingPrincipal, withheld.Net, payout, end,
                AutoRenewalPolicy: position.AutoRenewalPolicy),
        ];
    }

    /// <summary>
    /// PERIODIC maturity: accrue ONLY the final coupon window (last-paid-through → maturity), withhold
    /// that one flow, and pay out principal + that final net. The last coupon is paid together with
    /// the principal at maturity (02 §2.1). Re-accruing the whole term here would double-count the
    /// coupons already paid — the bug this branch exists to avoid.
    /// </summary>
    private static IReadOnlyList<DomainEvent> MatureFinalCoupon(
        DepositPosition position, DayCountConvention dayCount, int withholdingBasisPoints, RateSchedule schedule)
    {
        var lastPaidThrough = CouponBoundary(position, position.CouponsPaid);
        // Price the final coupon window over the resolved rate vector AND the principal timeline,
        // anchored at the deposit start so the rate step in force across [lastPaidThrough, maturity] is
        // the one applied (F.10) and a withdrawal inside that window splits the principal exactly (F.12,
        // bd babelstone-emtr). Return the principal still on deposit, not the original.
        var gross = schedule.AccrueGrossWindowOverPrincipal(
            position.PrincipalTimeline, position.StartDate, lastPaidThrough, position.MaturityDate, dayCount);
        var withheld = Withholding.Withhold(gross, withholdingBasisPoints);
        var payout = position.RemainingPrincipal + withheld.Net;

        return
        [
            new InterestAccrued(gross, position.MaturityDate),
            new WithholdingApplied(withheld.Tax, withheld.Net),
            new DepositMatured(position.RemainingPrincipal, withheld.Net, payout, position.MaturityDate,
                AutoRenewalPolicy: position.AutoRenewalPolicy),
        ];
    }

    /// <summary>ADVANCE maturity: principal only — interest was paid at t=0. Zero-interest payout.
    /// Returns the principal still on deposit (<see cref="DepositPosition.RemainingPrincipal"/>); a
    /// partial withdrawal is forbidden on ADVANCE (interest is pre-paid and cannot be re-based — bd
    /// babelstone-emtr), so this equals the original principal, but the uniform "maturity returns what
    /// is on deposit" rule leaves no payout path reading the pre-withdrawal principal.</summary>
    private static IReadOnlyList<DomainEvent> MatureAdvance(DepositPosition position) =>
    [
        new DepositMatured(position.RemainingPrincipal, Money.Zero, position.RemainingPrincipal, position.MaturityDate,
            AutoRenewalPolicy: position.AutoRenewalPolicy),
    ];

    /// <summary>
    /// The coupon boundary date <paramref name="couponIndex"/> months-cadences after the start, capped
    /// at the maturity date. With cadence <c>p</c> months, boundary <c>k</c> is
    /// <c>start.AddMonths(k × p)</c>; a boundary at or past maturity collapses onto the maturity date
    /// so the final (possibly short/stub) coupon runs exactly to maturity. Pure date arithmetic —
    /// no clock. The service uses this to derive the next coupon window from <c>CouponsPaid</c>.
    /// </summary>
    public static DateOnly CouponBoundary(DepositPosition position, int couponIndex)
    {
        var boundary = position.StartDate.AddMonths(couponIndex * position.PaymentPeriodMonths);
        return boundary >= position.MaturityDate ? position.MaturityDate : boundary;
    }

    // ---- early termination (02 §2.5) -----------------------------------------------------------

    /// <summary>
    /// Break a deposit before maturity and settle net of the configured penalty (02 §2.5). Pure: the
    /// position carries the principal/rate/start, the pack supplies the day-count and withholding rate,
    /// and the product's <see cref="EarlyTerminationPolicy"/> + the termination date are explicit inputs
    /// — no clock. The flow, in order:
    /// <list type="number">
    /// <item><b>Accrue</b> the gross interest earned over the elapsed period
    /// <c>[start, terminationDate]</c> on the resolved day-count (a <i>short</i> term, not the full one).</item>
    /// <item><b>Withhold</b> that ONE accrued flow (<see cref="Withholding.Withhold"/>) — flow-by-flow,
    /// never rate-scaled (fin-math §5.4). The realized net interest is this single flow's net.</item>
    /// <item><b>Select the band</b> first-match against the elapsed term and compute the penalty as the
    /// band's basis-point share of its <see cref="PenaltyBasis"/> (accrued interest, principal, or both).
    /// The penalty is computed on the GROSS accrued interest (the headline the band prices), never on the
    /// post-tax net — the share is rounded once at the Money boundary like every other amount.</item>
    /// <item><b>Settle</b> <c>principal + netAccrued − penalty</c>, then <b>floor</b>: the net settlement
    /// never falls below the policy floor (02 §2.5). When the floor binds, the EFFECTIVE penalty is
    /// reduced so the conservation <c>settlement = principal + netAccrued − penalty</c> still holds to the
    /// cent (the event records the effective penalty, not the pre-floor headline).</item>
    /// </list>
    /// Emits the SAME three-event shape AT_MATURITY uses for the interest flow — <see cref="InterestAccrued"/>
    /// + <see cref="WithholdingApplied"/> (so the withholding ledger and position fold the accrued flow exactly
    /// as any other) — then <see cref="DepositTerminatedEarly"/> carrying the principal, the resolved penalty,
    /// and the net settlement. One accumulation path per flow: the accrued interest folds via the Accrued+Withheld
    /// pair (it has no <see cref="InterestPaid"/>), and the terminated event itself carries no interest tally.
    /// </summary>
    /// <param name="position">The Active deposit being broken (principal, TAN, start date).</param>
    /// <param name="terminationDate">The as-of date the break is priced and accrued to — an INPUT, not a clock read.</param>
    /// <param name="policy">The product's early-termination policy (flat or banded, with optional floor).</param>
    /// <param name="dayCount">The pack-resolved day-count convention the elapsed accrual uses.</param>
    /// <param name="withholdingBasisPoints">The pack-resolved withholding rate, applied to the one accrued flow.</param>
    /// <param name="terminationReason">A stable, non-PII reason code recorded on the event (e.g. <c>CUSTOMER_REQUEST</c>).</param>
    /// <param name="schedule">The resolved rate VECTOR (F.10) the elapsed-period interest folds over
    /// — a step-up/amount-tiered <see cref="RateSchedule"/>, or <c>null</c> for the flat product.
    /// A deposit broken mid-<i>crescente</i> accrues only the steps it actually reached (the vector
    /// is clipped to the elapsed window). The RATE-REDUCTION penalty basis (F.11) reuses this same
    /// vector to recompute the elapsed accrual at the band's reduced rate.</param>
    public static IReadOnlyList<DomainEvent> DecideEarlyTermination(
        DepositPosition position, DateOnly terminationDate, EarlyTerminationPolicy policy,
        DayCountConvention dayCount, int withholdingBasisPoints, string terminationReason,
        RateSchedule? schedule = null)
    {
        // 1. Accrue the elapsed-period gross interest (start → termination), one flow folded over the
        //    resolved rate vector (flat for the common product). AccrueGross rejects a reversed
        //    interval, so a termination before the start date fails loud rather than emitting
        //    negative interest. This is the ACTUAL interest the deposit earned at its real rate(s).
        var rates = ScheduleOrFlat(position, schedule);
        var grossAccrued = rates.AccrueGross(
            position.RemainingPrincipal, position.StartDate, terminationDate, dayCount);

        // 2. Withhold that ONE flow — flow-by-flow (fin-math §5.4), never rate-scaled. Withholding is
        //    ALWAYS on the real gross the deposit earned, NEVER on a rate-reduced figure — the
        //    rate-reduction penalty (F.11) is a separate haircut on the gross, applied AFTER tax in
        //    the settlement conservation, so the depositor is taxed on what they earned and the
        //    penalty is what they forfeit.
        var withheld = Withholding.Withhold(grossAccrued, withholdingBasisPoints);

        // 3. Select the band first-match against the elapsed term, then compute the penalty on its basis.
        var elapsedDays = terminationDate.DayNumber - position.StartDate.DayNumber;
        var band = policy.ResolveBand(elapsedDays);
        var penalty = ComputePenalty(
            band, position.RemainingPrincipal, grossAccrued, rates, position.StartDate, terminationDate, dayCount);

        // 4. Settle principal + net accrued − penalty, then floor. The floor caps the EFFECTIVE penalty
        //    (never the principal/net legs) so the conservation settlement = principal + net − penalty
        //    still holds to the cent and the event records the penalty actually charged.
        var preFloorSettlement = position.RemainingPrincipal + withheld.Net - penalty;
        var (settlement, effectivePenalty) = ApplyFloor(preFloorSettlement, penalty, policy.Floor,
            position.RemainingPrincipal, withheld.Net);

        return
        [
            new InterestAccrued(grossAccrued, terminationDate),
            new WithholdingApplied(withheld.Tax, withheld.Net),
            new DepositTerminatedEarly(
                position.DepositId, position.RemainingPrincipal, effectivePenalty, settlement,
                terminationDate, terminationReason),
        ];
    }

    /// <summary>
    /// The penalty for a band: its basis-point share of the chosen basis (02 §2.5). The share is a
    /// single decimal computation rounded once at the Money boundary (ADR-PC-010 §P1–§P2) — never a
    /// rate scaled mid-calculation. The basis is the GROSS accrued interest, the principal, or both.
    /// </summary>
    private static Money ComputePenalty(
        EarlyTerminationBand band, Money principal, Money grossAccrued,
        RateSchedule rates, DateOnly start, DateOnly terminationDate, DayCountConvention dayCount)
    {
        // RATE-REDUCTION basis (F.11): the penalty is NOT a basis-point share of an amount — it is
        // the interest the depositor FORFEITS by being repriced to a lower rate for the elapsed
        // period. penalty = J(original) − J(reduced), recomputing the SAME elapsed accrual at the
        // band's reduced rate (a flat reduced rate over the elapsed window — the "penalty rate" a PT
        // deposit drops to on an early break, 02 §2.5 / fin-math §5). The depositor still keeps the
        // reduced interest; only the difference is the penalty. Computed on GROSS (pre-tax) so it
        // composes with the same settlement conservation as every other basis; withholding upstream
        // is on the real (original) gross, never on this reduced figure (fin-math §5.4).
        if (band.Basis == PenaltyBasis.RateReduction)
        {
            var reducedRateBps = band.ReducedRateBasisPoints
                ?? throw new InvalidOperationException(
                    "An early-termination band with basis RATE_REDUCTION must carry a reduced_rate_basis_points " +
                    "(the rate the elapsed interest is recomputed at); none was configured.");

            // The reduced-rate gross over the SAME elapsed window. A flat reduced rate is the PT
            // convention; we fold it as a flat schedule so the day-count/rounding is identical to
            // the original accrual, and the subtraction is exact to the cent.
            var reducedGross = RateSchedule.Flat(reducedRateBps)
                .AccrueGross(principal, start, terminationDate, dayCount);

            // The forfeit is non-negative when reduced_rate <= original effective rate (the only
            // sensible configuration — a "reduction"). Guard it: a reduced rate ABOVE the original
            // would make J(reduced) > J(original) and drive the penalty negative (a bonus), which the
            // non-negative PenaltyAmount contract forbids — fail loud rather than record it.
            if (reducedGross.Cents > grossAccrued.Cents)
            {
                throw new InvalidOperationException(
                    $"RATE_REDUCTION penalty is negative: the reduced-rate interest ({reducedGross.Cents} cents) " +
                    $"exceeds the actual accrued interest ({grossAccrued.Cents} cents). The reduced rate " +
                    $"({reducedRateBps} bps) must not exceed the deposit's effective rate — a reduction lowers " +
                    "the rate, it never raises it. Refusing to record a negative (bonus) penalty.");
            }
            return grossAccrued - reducedGross;
        }

        var basisAmount = band.Basis switch
        {
            PenaltyBasis.AccruedInterest => grossAccrued,
            PenaltyBasis.Principal => principal,
            // BOTH: the share applies to (principal + gross accrued interest).
            _ => principal + grossAccrued,
        };

        // share = basis_cents × penalty_bps / 10000, rounded once HALF_EVEN at the cents boundary.
        return Money.FromCents((decimal)basisAmount.Cents * band.PenaltyBasisPoints / 10_000);
    }

    /// <summary>
    /// Enforce the policy floor (02 §2.5): the net settlement never falls below it. When the floor
    /// binds, the EFFECTIVE penalty is reduced to <c>principal + net − floor</c> so the conservation
    /// <c>settlement = principal + net − penalty</c> stays exact to the cent (the floor is realised by
    /// charging less penalty, never by inventing money on the principal/net legs). With no floor, the
    /// pre-floor settlement and headline penalty pass through unchanged.
    /// </summary>
    /// <remarks>
    /// A floor is a principal-protection MINIMUM (02 §2.5: "typically principal less any pack-permitted
    /// principal haircut", i.e. <c>floor &lt;= principal &lt;= principal + net</c>), so a well-formed floor
    /// only ever reduces the penalty. A misconfigured floor ABOVE the natural full payout
    /// (<c>floor &gt; principal + net</c>) would drive the effective penalty negative — the engine would
    /// have to invent money to reach the floor and would record a non-conforming negative penalty on
    /// <see cref="DepositTerminatedEarly"/> (whose <c>PenaltyAmount</c> is documented non-negative). That
    /// is a policy misconfiguration the floor semantics do not contemplate, so we fail loud rather than
    /// settle a financially nonsensical leg — mirroring <see cref="EarlyTerminationPolicy.ResolveBand"/>'s
    /// refusal to default to a silent zero penalty.
    /// </remarks>
    /// <exception cref="InvalidOperationException">If a configured floor exceeds the natural maximum
    /// payout (<c>principal + net</c>), which would require a negative penalty to honour.</exception>
    private static (Money Settlement, Money EffectivePenalty) ApplyFloor(
        Money preFloorSettlement, Money penalty, Money? floor, Money principal, Money net)
    {
        if (floor is { } floorValue && preFloorSettlement.Cents < floorValue.Cents)
        {
            var naturalMax = principal + net;
            if (floorValue.Cents > naturalMax.Cents)
            {
                throw new InvalidOperationException(
                    $"Early-termination floor ({floorValue.Cents} cents) exceeds the natural maximum payout " +
                    $"of principal + net accrued interest ({naturalMax.Cents} cents); honouring it would " +
                    "require a negative penalty (inventing money). Refusing — a floor is a principal-protection " +
                    "minimum (02 §2.5), not a top-up above the full payout.");
            }

            // Settlement is lifted to the floor; the penalty actually charged is whatever brings the
            // (principal + net) payout down to the floor — i.e. the floor absorbs the excess penalty.
            // Guarded above, so effectivePenalty is non-negative (>= Money.Zero).
            var effectivePenalty = naturalMax - floorValue;
            return (floorValue, effectivePenalty);
        }

        return (preFloorSettlement, penalty);
    }

    // ---- auto-renewal (02 §2.4.4) --------------------------------------------------------------

    /// <summary>
    /// The renewal TAN (in basis points) and the rate-sheet version it came from, resolved by policy
    /// (02 §2.4.4). Pure: the service does the I/O (re-resolves the sheet at the renewal moment) and
    /// hands BOTH the freshly-resolved rate AND the closing deposit's original rate in; this method
    /// only PICKS between them per the policy the closing position carries — no clock, no I/O.
    /// <list type="bullet">
    /// <item><b>SAME_TERM_CURRENT_RATE</b> — the bank's then-current standard rate: take the freshly
    /// re-resolved <paramref name="currentTanBasisPoints"/> / <paramref name="currentRateSheetVersionId"/>.</item>
    /// <item><b>SAME_TERM_SAME_RATE</b> — the original rate: carry the closing deposit's
    /// <see cref="DepositPosition.TanBasisPoints"/> and <see cref="DepositPosition.RateSheetVersionId"/>
    /// forward unchanged. 02 §2.4.4 pack-RESTRICTS this policy ("less common, pack-restricted"); that
    /// restriction is now a pack primitive (bd babelstone-k6r8.6, the babelstone-k4yr follow-up): the
    /// pack's <c>primitives/renewal-policies.yaml</c> <c>same_term_same_rate.permitted_for</c> set
    /// declares which families may use it, and pack-validate's depth-4 check rejects a variant
    /// declaring it where the pack does not permit it. This decider faithfully PRICES whatever
    /// renewal policy the constituted deposit already carries — the policy could only have been
    /// constituted if the variant cleared the pack restriction, so no re-check is needed here.</item>
    /// </list>
    /// <c>NONE</c> never reaches here (the service rejects it before deciding — there is no renewal to price).
    /// </summary>
    public static (int TanBasisPoints, string RateSheetVersionId) ResolveRenewalRate(
        DepositPosition closing, int currentTanBasisPoints, string currentRateSheetVersionId) =>
        closing.AutoRenewalPolicy switch
        {
            RenewalSameTermSameRate => (closing.TanBasisPoints, closing.RateSheetVersionId),
            // SAME_TERM_CURRENT_RATE is the only other renewable policy (NONE is rejected upstream).
            _ => (currentTanBasisPoints, currentRateSheetVersionId),
        };

    /// <summary>
    /// Build the new instance's <see cref="DepositConstituted"/> for an auto-renewal (02 §2.4.4 step 2):
    /// a fresh deposit constituted from the rolled-over principal at the policy-resolved TAN/version, for
    /// the SAME term, interest variant, cadence, and renewal policy as the closing deposit. The new start
    /// date is the renewal date and the new maturity is <c>renewalDate + termDays</c> — derived here, not
    /// recomputed downstream. Pure: the rolled-over principal, resolved rate, and renewal date are explicit
    /// inputs (the service settles and threads the <c>causation_id</c> → closing <c>DepositMatured</c>).
    /// </summary>
    /// <param name="role">The EFFECTIVE pricing role to stamp on the renewed instance — the closing
    /// deposit's <see cref="DepositPosition.Role"/> carried forward, with the pre-field-deposit
    /// fallback (empty → <c>standard</c>) ALREADY applied by the service (bd babelstone-mtto.5). It is
    /// the SAME role the service re-resolved the rate against, so the renewed instance prices and
    /// records the one role — chain preservation across renewal generations.</param>
    /// <param name="fundingAccount">The opaque funding-account token to stamp on the renewed instance
    /// — the closing deposit's <see cref="DepositPosition.FundingAccount"/> carried forward (the
    /// service settles the rollover debit against this SAME reference and rejects an empty one before
    /// reaching here, bd babelstone-mtto.5).</param>
    public static DepositConstituted DecideRenewalConstitution(
        DepositPosition closing, Guid newDepositId, Money rolloverPrincipal,
        int tanBasisPoints, string rateSheetVersionId, DateOnly renewalDate,
        string role, string fundingAccount) =>
        new(
            DepositId: newDepositId,
            Principal: rolloverPrincipal,
            TanBasisPoints: tanBasisPoints,
            RateSheetVersionId: rateSheetVersionId,
            TermDays: closing.TermDays,
            StartDate: renewalDate,
            MaturityDate: renewalDate.AddDays(closing.TermDays),
            InterestVariant: closing.InterestVariant,
            AutoRenewalPolicy: closing.AutoRenewalPolicy,
            PaymentPeriodMonths: closing.PaymentPeriodMonths,
            // The renewed instance is the SAME structural product as the closing deposit, so carry
            // the closing position's catalogue code forward (bd babelstone-v794). For a deposit
            // constituted before v794 the closing code is "" and the renewed instance inherits "" —
            // the renewal cannot manufacture a code the original never carried.
            ProductCode: closing.ProductCode,
            // The role + funding-account token carried forward from the closing deposit so the
            // renewed instance records the SAME (product, role) it was repriced against and the SAME
            // funding reference the rollover debited — chain preservation across renewal generations
            // (bd babelstone-mtto.5). The effective role (with the pre-field fallback) and the funding
            // token are resolved by the service and passed in; the decider stays pure.
            Role: role,
            FundingAccount: fundingAccount,
            // The F.12 partial-withdrawal policy carried forward from the closing deposit (bd
            // k6r8.8/qze9): the renewed instance is the SAME product, so it inherits the SAME pinned
            // gates — chain preservation across renewal generations, exactly like product/role/funding.
            MinWithdrawalCents: closing.MinWithdrawalCents,
            MinRemainingBalanceCents: closing.MinRemainingBalanceCents,
            CarenciaDays: closing.CarenciaDays);

    /// <summary>
    /// Build the <see cref="DepositRenewed"/> link (02 §2.4.4 step 3) carrying the closing↔new deposit ids
    /// and the new instance's pinned facts, for direct old→new lookup. Pure: every field is read off the
    /// closing position and the already-decided new constitution — no clock, no I/O.
    /// </summary>
    public static DepositRenewed DecideRenewalLink(DepositPosition closing, DepositConstituted renewed) =>
        new(
            DepositId: closing.DepositId,
            NewDepositId: renewed.DepositId,
            RolloverPrincipal: renewed.Principal,
            NewRateSheetVersionId: renewed.RateSheetVersionId,
            NewTanBasisPoints: renewed.TanBasisPoints,
            NewTermDays: renewed.TermDays,
            RenewalDate: renewed.StartDate,
            NewMaturityDate: renewed.MaturityDate);
}
