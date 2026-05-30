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
    string Actor);

/// <summary>Mature a constituted deposit: accrue → withhold → pay out the AT_MATURITY single flow.</summary>
/// <param name="PayoutAccount">The legacy current account credited the total payout (settlement).</param>
public sealed record MatureDepositCommand(
    Guid DepositId,
    DateTimeOffset MaturedAt,
    string PayoutAccount,
    string Actor);
