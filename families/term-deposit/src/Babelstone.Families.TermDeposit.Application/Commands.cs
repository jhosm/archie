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
/// engine's injected <c>TimeProvider</c>, the impure-shell clock). Replay stability holds because the
/// derived start date is STAMPED onto <c>DepositConstituted</c> and folded VERBATIM by
/// <c>DepositConstitutedHandler.Apply</c> — a full fold/rebuild reproduces the identical value and
/// never re-reads the clock, so the start date is never re-derived on a replay. (The Idempotency-Key
/// dedup, ADR-PC-029 slot 4, is the separate COMMAND-retry guarantee: a replayed constitution with the
/// same <see cref="CommandId"/> returns the original outcome with no second append; it prevents a
/// second clock read on a retry, but the load-bearing fold guarantee is the event-captured value.)
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

/// <summary>Withdraw part of a constituted deposit's principal before maturity (F.12; 02 §2.4.1):
/// reduce the principal by a fixed amount, leaving the deposit OPEN and Active. A PRINCIPAL reduction
/// ONLY — no interest, withholding, or settlement flow (unlike early termination, which CLOSES the
/// deposit and settles). The product's <see cref="PartialWithdrawalPolicy"/> (minimum withdrawal /
/// minimum remaining balance / carência lock-up) is per-PRODUCT config the service resolves from the
/// deposit's product config — not a command input — and the pure decider takes it as an explicit input
/// (ADR-PC-021 §D3). Withdrawing the whole balance is a termination (F.4), which the decider refuses.</summary>
/// <param name="WithdrawnAt">The instant the withdrawal fires: its DATE is the as-of withdrawal date the
/// event records and the carência lock-up is measured against. Passed as an INPUT so the decision stays
/// pure and replayable (no clock in the decider).</param>
/// <param name="WithdrawnAmountCents">The principal to take out, in integer cents.</param>
/// <param name="CommandId">The ingress idempotency key (ADR-PC-029 slot 4): the append dedupes on it
/// in-transaction (the <c>command_dedup</c> INSERT), so an at-least-once retry of the SAME withdrawal
/// returns the original outcome rather than double-appending. Mandatory — UNLIKE maturity / coupon
/// (lifecycle-guarded, one-shot), a partial withdrawal is REPEATABLE (it leaves the deposit Active), so
/// a non-idempotent retry would withdraw twice; the idempotency key is the only safe contract for it.</param>
public sealed record PartialWithdrawCommand(
    Guid DepositId,
    DateTimeOffset WithdrawnAt,
    long WithdrawnAmountCents,
    string Actor,
    Guid CommandId);

/// <summary>
/// Record the GDPR Article 17 erasure fact on a deposit (bd babelstone-nzw6): append
/// <see cref="PersonalDataErasureRequested"/> so the deposit folds to <c>Erased</c>. The actual
/// crypto-shredding of the subject's key (<c>IPiiKeyStore.DestroyKeyAsync</c>, ADR-PC-004 §P3) is the
/// caller's responsibility — it runs in the impure HOST shell (the OpenBao boundary lives in the
/// engine host, not this PII-free Application layer) BEFORE this command is issued. So this command
/// carries ONLY structural facts: the deposit id, a salted one-way subject pseudonym (never the raw
/// subject id — ADR-IC-016 §8 / ADR-PC-004 §P2), the erasure date, and a reason code.
/// </summary>
/// <param name="SubjectPseudonym">A salted one-way hash of the data-subject id, derived by the host —
/// an opaque audit/correlation reference, NEVER the raw subject id.</param>
/// <param name="ErasedAt">The instant erasure took effect; its DATE is recorded on the event (audit
/// lineage). Passed as an input so the append stays clock-free.</param>
/// <param name="ErasureReason">A stable machine code (e.g. <c>GDPR_ARTICLE_17</c>) — never PII.</param>
/// <param name="CommandId">The ingress idempotency key (ADR-PC-029 slot 4): the append dedupes on it
/// in-transaction (the <c>command_dedup</c> INSERT), so an at-least-once retry of the SAME erasure
/// returns the original outcome rather than double-appending. Mandatory for erasure — key destruction
/// is irreversible, so a non-idempotent retry must be impossible.</param>
public sealed record ErasePersonalDataCommand(
    Guid DepositId,
    string SubjectPseudonym,
    DateTimeOffset ErasedAt,
    string ErasureReason,
    string Actor,
    Guid CommandId);

// The monolithic RenewDepositCommand (which drove the un-idempotent, three-step, cross-stream
// RenewAsync) is RETIRED (bd babelstone-mtto PR B). Renewal is now two idempotent engine operations
// with the maturity leg dropped — see ConstituteRenewalCommand / LinkRenewalCommand in
// RenewalCommands.cs, driven by ConstituteRenewalAsync / LinkRenewalAsync.
