namespace Babelstone.Families.TermDeposit.Application;

// The term-deposit command surface (E.3, ADR-PC-021). A command carries only per-deposit
// facts; the pinned pack + its primitive bindings are engine-instance configuration held by
// the service (ADR-PC-009 per-instance pinning is a service-level stand-in for the walking
// skeleton — a config registry deriving them per deposit is later work). The resolved TAN
// and rate_sheet_version_id are NOT command inputs — the service resolves them at
// constitution from the rate sheet (ADR-PC-008 §P3) and stamps them onto the event.

/// <summary>
/// A resolved commercial-eligibility verdict the constitution saga gathered upstream and passes
/// on the constitution command (ADR-PC-024 §1–§2). It asserts a <i>fact</i> — "an upstream
/// authority evaluated this product-specific predicate for this customer at <see cref="EvaluatedAt"/>
/// and it is <see cref="Satisfied"/> / not." The MEANING of each predicate (what counts as "new
/// money", the look-back window, what a "salary domiciliation" is) is entirely upstream/pack-owned;
/// the engine treats the verdict as OPAQUE and never re-evaluates it. The triple carries NO PII:
/// <see cref="EvidenceRef"/> is a resolvable reference, never identity data (ADR-PC-024 §1, the
/// PII-by-reference rule inherited from the signal-contract family).
/// </summary>
/// <param name="Satisfied">Whether the upstream authority found the predicate satisfied.</param>
/// <param name="EvidenceRef">An opaque reference to the upstream evidence — NOT identity data.</param>
/// <param name="EvaluatedAt">When the upstream authority took the verdict (audit lineage / freshness).</param>
public sealed record PreconditionVerdict(
    bool Satisfied,
    string EvidenceRef,
    DateTimeOffset EvaluatedAt);

/// <summary>Open a term deposit: the principal, term, and pricing inputs fixed at constitution.</summary>
/// <param name="ProductId">The variant id the rate sheet prices, e.g. <c>dpz_pt_12m_juros_venc</c>.</param>
/// <param name="Role">The pricing role resolved from the deposit origin, e.g. <c>standard</c>.</param>
/// <param name="ConstitutedAt">The instant the sheet is resolved as-of and the event's valid time.</param>
/// <param name="FundingAccount">The legacy current account debited for the principal (settlement).</param>
/// <param name="PaymentPeriodMonths">The coupon cadence in months for PERIODIC deposits — 1
/// (monthly) or 3 (quarterly), the only cadences v1 prices (02 §2.1, enforced by the CUE
/// schema). Ignored (and conventionally 0) for AT_MATURITY and ADVANCE, which have no coupons.</param>
/// <param name="Preconditions">The resolved commercial-eligibility verdicts the saga gathered
/// upstream (ADR-PC-024), keyed by the engine's closed verdict-key taxonomy (e.g.
/// <c>is_new_money</c>). The decider refuses the constitution when a verdict the product's
/// <c>required_preconditions</c> demands is absent here or <c>Satisfied == false</c> — a PURE
/// function of these verdicts, with no in-engine evaluation (ADR-PC-024 §3–§5). Defaults to
/// empty: v1 launch products are not eligibility-gated (02 §4), so most commands carry none.</param>
/// <param name="CommandId">The caller's deterministic command id (ADR-PC-029 slot 4) — in
/// practice the saga's <c>saga_outbox</c> row id, supplied by the dispatcher as the
/// <c>Idempotency-Key</c>. The constitution append is idempotent on it: a replay returns the
/// original <c>commit_sequence</c> with no second append (and, before any append, the endpoint
/// short-circuits so the eager settlement is not re-run). The HTTP boundary makes it MANDATORY
/// (a missing/malformed key is a 400); the type stays nullable only for direct in-process
/// callers (family unit tests that construct the command without exercising idempotency).</param>
public sealed record ConstituteDepositCommand(
    Guid DepositId,
    long PrincipalCents,
    string ProductId,
    string Role,
    int TermDays,
    DateOnly StartDate,
    DateTimeOffset ConstitutedAt,
    string InterestVariant,
    string AutoRenewalPolicy,
    string FundingAccount,
    string Actor,
    int PaymentPeriodMonths = 0,
    IReadOnlyDictionary<string, PreconditionVerdict>? Preconditions = null,
    Guid? CommandId = null);

/// <summary>
/// The MINIMAL constitution request the saga now sends (Fork B rework, bd t7o3.11 / 3k10 / c8d8):
/// a product code, the principal, the funding account, and the deposit id — and NOTHING about the
/// product's SHAPE. The engine resolves the structural facts (term / interest variant / renewal
/// policy / coupon cadence / pricing role) from its deployed product-config store at constitution,
/// so the orchestrator carries no product-family knowledge — the maintainer's Q2 choice. The engine
/// is the single home of product config, and the only authority that knows what a product code means.
/// </summary>
/// <remarks>
/// <para>
/// <b>The structural facts are resolved engine-side, in the SAME transaction as the rate-sheet
/// resolve (ADR-PC-008 §S2 / ADR-PC-009).</b> The service looks up the product config (term / variant
/// / renewal policy / cadence / default role), derives the start date from
/// <see cref="ConstitutedAt"/> (the engine is now the constitution authority — see §P5 below), then
/// runs the existing resolve→decide→append choreography. An unknown product code fails LOUD, never a
/// silent default — the same fail-loud discipline as an unpriced (product, role).
/// </para>
/// <para>
/// <b>start_date / replay-stability (ADR-PC-010 §P5).</b> Where the rejected stand-in PINNED the start
/// date at the orchestrator edge so the saga's command bytes carried no clock, the engine is now the
/// event author and derives the start date from <see cref="ConstitutedAt"/> (host-stamped by the
/// engine's injected <c>TimeProvider</c>, the impure-shell clock). Replay stability is preserved by
/// the Idempotency-Key dedup (ADR-PC-029 slot 4): a replayed constitution with the same
/// <see cref="CommandId"/> returns the original outcome with NO second append, so the start date is
/// never re-derived on a replay.
/// </para>
/// <para>
/// <b>No PII (ADR-PC-004 §P2).</b> Every field is a structural reference or an integer-cents scalar —
/// the catalogue product code, integer-cents principal, the opaque funding-account token, the
/// process-id deposit id. NEVER a raw IBAN / NIF / name.
/// </para>
/// </remarks>
/// <param name="DepositId">The stream/aggregate id (= the saga's process_id), so the relayed
/// <c>DepositConstituted</c> carries <c>ce_subject = process_id</c> and the orchestrator correlates it.</param>
/// <param name="ProductId">The product code the engine resolves both the SHAPE and the rate from.</param>
/// <param name="PrincipalCents">The deposit principal in integer cents.</param>
/// <param name="FundingAccount">The opaque funding-account token (a reference, not an IBAN).</param>
/// <param name="ConstitutedAt">The instant the sheet is resolved as-of, the event's valid time, and
/// the source of the derived start date. Host-stamped by the engine's clock when the caller omits it.</param>
/// <param name="Actor">The acting principal recorded on the append (e.g. <c>mcp:dev</c>).</param>
/// <param name="CommandId">The deterministic command id (the saga_outbox row id) — the Idempotency-Key
/// the constitution append dedups on (ADR-PC-029 slot 4).</param>
/// <param name="Role">An OPTIONAL pricing-role override; when null the product config's default role
/// (v1: <c>standard</c>) is used. The orchestrator never sends this — it is for direct callers.</param>
/// <param name="Preconditions">The resolved commercial-eligibility verdicts (ADR-PC-024), if any.</param>
public sealed record MinimalConstituteDepositRequest(
    Guid DepositId,
    string ProductId,
    long PrincipalCents,
    string FundingAccount,
    DateTimeOffset ConstitutedAt,
    string Actor,
    Guid? CommandId = null,
    string? Role = null,
    IReadOnlyDictionary<string, PreconditionVerdict>? Preconditions = null);

/// <summary>Mature a constituted deposit: accrue → withhold → pay out the AT_MATURITY single flow.</summary>
/// <param name="PayoutAccount">The legacy current account credited the total payout (settlement).</param>
public sealed record MatureDepositCommand(
    Guid DepositId,
    DateTimeOffset MaturedAt,
    string PayoutAccount,
    string Actor);

/// <summary>Pay one PERIODIC coupon: accrue the next coupon window's interest, withhold that one
/// flow, and credit the net to the depositor's current account (02 §2.1 <c>CF(k) = +J_k</c>). The
/// coupon window is derived by the service from the deposit's start date, payment cadence, and the
/// number of coupons already paid — it is not a command input (the engine owns the schedule).
/// Coupons are triggered manually here, exactly as maturity is; the time-based scheduler that
/// auto-fires them on due dates is deferred to A.8b.</summary>
/// <param name="PayoutAccount">The legacy current account credited the coupon net (settlement).</param>
public sealed record PayInterestCommand(
    Guid DepositId,
    DateTimeOffset PaidAt,
    string PayoutAccount,
    string Actor);

/// <summary>Break a constituted deposit before maturity (02 §2.5): accrue the elapsed-period interest,
/// withhold that one flow, apply the product's configured penalty (flat or banded, with optional floor)
/// to the right basis, and settle the net payout to the depositor's current account. The penalty policy
/// is per-PRODUCT config the bank's pricing team owns (it rides on the product config, not a command
/// input — the service resolves it, mirroring how the day-count/withholding primitives are resolved).
/// Termination is triggered MANUALLY here, exactly as maturity is.</summary>
/// <param name="TerminatedAt">The instant the break fires: its DATE is the as-of termination date the
/// elapsed interest accrues to and the penalty band is selected against. Passed as an INPUT so the
/// decision stays pure and replayable (no clock in the decider).</param>
/// <param name="PayoutAccount">The legacy current account credited the net settlement (settlement).</param>
/// <param name="TerminationReason">A stable, non-PII reason code recorded on the event
/// (e.g. <c>CUSTOMER_REQUEST</c>) — never anything about the customer (ADR-PC-004 §P2).</param>
public sealed record TerminateEarlyCommand(
    Guid DepositId,
    DateTimeOffset TerminatedAt,
    string PayoutAccount,
    string TerminationReason,
    string Actor);

/// <summary>Auto-renew a maturing deposit (02 §2.4.4): mature the closing instance, constitute a
/// fresh engine-native instance from the rolled-over principal at the policy-resolved rate, and link
/// the two with <c>DepositRenewed</c>. The renewal branches on the closing deposit's
/// <c>auto_renewal_policy</c> (folded onto the position from its <c>DepositConstituted</c>), so the
/// policy is NOT a command input. Renewal is triggered MANUALLY here, exactly as maturity is — the
/// time-based scheduler that auto-fires it on the renewal date is H.3, deliberately out of scope.</summary>
/// <param name="ProductId">The variant id the rate sheet re-prices the new instance against for the
/// SAME_TERM_CURRENT_RATE policy (the position carries only the resolved TAN, never the product/role
/// keys, so the caller supplies them — mirroring <see cref="ConstituteDepositCommand"/>).</param>
/// <param name="Role">The pricing role for the re-resolution (e.g. <c>standard</c>).</param>
/// <param name="RenewedAt">The instant the renewal fires: the new sheet is resolved as-of here, and
/// it is the closing maturity's and the new constitution's valid time. Its DATE is the renewal date.</param>
/// <param name="NewDepositId">The fresh stream id the renewed instance is constituted under. Caller-
/// supplied (not engine-generated) so the renewal is a deterministic, replayable command — the new
/// id is the same on replay, and the <c>DepositRenewed</c> link is stable.</param>
/// <param name="PayoutAccount">The legacy current account credited the closing maturity payout.</param>
/// <param name="FundingAccount">The legacy current account debited the rolled-over principal of the
/// new instance (the principal settles out at maturity and back in at the new constitution, so each
/// leg's money movement matches its standalone command).</param>
public sealed record RenewDepositCommand(
    Guid DepositId,
    string ProductId,
    string Role,
    DateTimeOffset RenewedAt,
    Guid NewDepositId,
    string PayoutAccount,
    string FundingAccount,
    string Actor);
