using Babelstone.Families.TermDeposit;

namespace Babelstone.Engine.Api;

// The deposits HTTP contract (ADR-PC-021 §D5 boundary). snake_case on the wire (the host's
// JSON options), money as integer cents — never a nested object or a float (ADR-PC-010 §P1).

/// <summary>Constitute a deposit. Per-deposit facts only; the TAN is resolved from the rate sheet, not supplied.</summary>
/// <param name="PaymentPeriodMonths">PERIODIC coupon cadence in months (1 or 3); omit/0 for
/// AT_MATURITY and ADVANCE. Optional so the AT_MATURITY walking-skeleton callers stay unchanged.</param>
public sealed record ConstituteDepositRequest(
    long PrincipalCents,
    string ProductId,
    string Role,
    int TermDays,
    DateOnly StartDate,
    string InterestVariant,
    string AutoRenewalPolicy,
    string FundingAccount,
    Guid? DepositId = null,
    DateTimeOffset? ConstitutedAt = null,
    string? Actor = null,
    int PaymentPeriodMonths = 0);

/// <summary>The constitution outcome — the assigned id and lifecycle state (synchronous in the walking skeleton).</summary>
public sealed record ConstituteDepositResponse(Guid DepositId, string Status);

/// <summary>Mature a constituted deposit. The instant is host-stamped if omitted.</summary>
public sealed record MatureDepositRequest(
    DateTimeOffset? MaturedAt = null,
    string? PayoutAccount = null,
    string? Actor = null);

/// <summary>Pay one PERIODIC coupon. The coupon window is derived by the engine from the deposit's
/// schedule and the coupons already paid — not supplied here. The instant is host-stamped if omitted.</summary>
public sealed record PayInterestRequest(
    DateTimeOffset? PaidAt = null,
    string? PayoutAccount = null,
    string? Actor = null);

/// <summary>The folded deposit position — the <c>deposit_position</c> resource, money as integer cents.</summary>
public sealed record DepositPositionResponse(
    Guid DepositId,
    long PrincipalCents,
    int TanBasisPoints,
    string RateSheetVersionId,
    int TermDays,
    DateOnly StartDate,
    DateOnly MaturityDate,
    string InterestVariant,
    string AutoRenewalPolicy,
    int PaymentPeriodMonths,
    long AccruedGrossInterestCents,
    long WithholdingToDateCents,
    long NetInterestCents,
    long TotalPayoutCents,
    int CouponsPaid,
    string Lifecycle)
{
    public static DepositPositionResponse From(DepositPosition p) => new(
        DepositId: p.DepositId,
        PrincipalCents: p.Principal.Cents,
        TanBasisPoints: p.TanBasisPoints,
        RateSheetVersionId: p.RateSheetVersionId,
        TermDays: p.TermDays,
        StartDate: p.StartDate,
        MaturityDate: p.MaturityDate,
        InterestVariant: p.InterestVariant,
        AutoRenewalPolicy: p.AutoRenewalPolicy,
        PaymentPeriodMonths: p.PaymentPeriodMonths,
        AccruedGrossInterestCents: p.AccruedGrossInterest.Cents,
        WithholdingToDateCents: p.WithholdingToDate.Cents,
        NetInterestCents: p.NetInterest.Cents,
        TotalPayoutCents: p.TotalPayout.Cents,
        CouponsPaid: p.CouponsPaid,
        Lifecycle: p.Lifecycle.ToString());
}
