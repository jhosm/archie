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
