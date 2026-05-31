namespace Babelstone.Families.TermDeposit.Application;

// The term-deposit command surface (E.3, ADR-PC-021). A command carries only per-deposit
// facts; the pinned pack + its primitive bindings are engine-instance configuration held by
// the service (ADR-PC-009 per-instance pinning is a service-level stand-in for the walking
// skeleton — a config registry deriving them per deposit is later work). The resolved TAN
// and rate_sheet_version_id are NOT command inputs — the service resolves them at
// constitution from the rate sheet (ADR-PC-008 §P3) and stamps them onto the event.

/// <summary>Open a term deposit: the principal, term, and pricing inputs fixed at constitution.</summary>
/// <param name="ProductId">The variant id the rate sheet prices, e.g. <c>dpz_pt_12m_juros_venc</c>.</param>
/// <param name="Role">The pricing role resolved from the deposit origin, e.g. <c>standard</c>.</param>
/// <param name="ConstitutedAt">The instant the sheet is resolved as-of and the event's valid time.</param>
/// <param name="FundingAccount">The legacy current account debited for the principal (settlement).</param>
/// <param name="PaymentPeriodMonths">The coupon cadence in months for PERIODIC deposits — 1
/// (monthly) or 3 (quarterly), the only cadences v1 prices (02 §2.1, enforced by the CUE
/// schema). Ignored (and conventionally 0) for AT_MATURITY and ADVANCE, which have no coupons.</param>
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
    int PaymentPeriodMonths = 0);

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
