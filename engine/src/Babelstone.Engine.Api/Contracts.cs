using Babelstone.Engine;
using Babelstone.EventStore;
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

/// <summary>
/// The constitution outcome — the assigned id, lifecycle state (synchronous in the walking skeleton),
/// and <c>CommitSequence</c>: the per-stream version the append reached (ADR-IC-005 §P3). A caller
/// hands this straight back as the <c>If-Min-Sequence</c> token on the follow-up
/// <c>GET /v1/deposits/{id}</c> to get read-your-writes — the engine folds the stream rather than
/// serving a read-model row the projector has not yet caught up to.
/// </summary>
public sealed record ConstituteDepositResponse(Guid DepositId, string Status, long CommitSequence);

/// <summary>
/// The acknowledgement returned by the ASYNCHRONOUS command surface (I.1, bd babelstone-pxj9) — the
/// <c>202 Accepted</c> body of ADR-IC-006 §Context / Document 05 §Step-0. The host has accepted the
/// command and is dispatching it through the engine command path on a background task; it has NOT
/// blocked on completion. The caller follows progress on <see cref="StreamUrl"/> — the SSE endpoint
/// (<c>GET /v1/processes/{process_id}/stream</c>) that streams <see cref="ProcessSnapshot"/> updates
/// until the process reaches a terminal state.
/// </summary>
/// <param name="DepositId">The aggregate id the command will affect (assigned up front, like the
/// synchronous path), so the caller can reference the deposit before the dispatch completes.</param>
/// <param name="ProcessId">The host-assigned process identity, also embedded in <see cref="StreamUrl"/>.</param>
/// <param name="Status">Always <c>PROCESSING</c> at acceptance — the lifecycle is reported on the stream.</param>
/// <param name="StreamUrl">The relative SSE URL to subscribe to for progress (ADR-IC-006 §Context).</param>
public sealed record CommandAcceptedResponse(Guid DepositId, Guid ProcessId, string Status, string StreamUrl);

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

/// <summary>
/// The deposit resource (ADR-PC-021 §D5 boundary), money as integer cents. There is ONE deposit
/// resource — <c>GET /v1/deposits/{id}</c>; whether the answer is served from the denormalized
/// read-model row (the fast, eventually-consistent default) or an authoritative fold of the event
/// stream (the read-your-writes fallback) is the engine's private business, NOT a separate URL —
/// storage/mechanism never appears in the path. Both paths fill this SAME shape, so the wire response
/// is identical whichever served it: <see cref="FromReadModel"/> from the projection,
/// <see cref="FromFold"/> from the live aggregate.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="LastSequence"/> is the per-stream version the answer reflects (ADR-IC-005 §P3 — the
/// read-after-write barrier the caller passes back as <c>If-Min-Sequence</c>); <see cref="LastUpdated"/>
/// is the producing event's transaction_time (event-derived, for honest staleness display).
/// <see cref="Sor"/> is the ADR-PC-018 §6.2 routing truth (always <c>engine</c> for an
/// engine-materialised deposit). Two product keys appear under their honest names:
/// <see cref="RateSheetVersionId"/> (the price/version key) and <see cref="ProductCode"/> (the
/// catalogue structural code — the queryable "which product" dimension, PROSPECTIVE-ONLY per bd
/// babelstone-v794: pre-v794 deposits carry the "" default). No PII (ADR-PC-004 §P2) — structural
/// facts only.
/// </para>
/// </remarks>
public sealed record DepositResponse(
    Guid DepositId,
    string Sor,
    long PrincipalCents,
    int TanBasisPoints,
    string RateSheetVersionId,
    string ProductCode,
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
    string Lifecycle,
    long LastSequence,
    DateTimeOffset LastUpdated)
{
    /// <summary>From the denormalized read-model row — the fast, eventually-consistent default path.</summary>
    public static DepositResponse FromReadModel(DepositReadModelRow r) => new(
        DepositId: r.StreamId,
        Sor: r.Sor,
        PrincipalCents: r.PrincipalCents,
        TanBasisPoints: r.TanBasisPoints,
        RateSheetVersionId: r.RateSheetVersionId,
        ProductCode: r.ProductCode,
        TermDays: r.TermDays,
        StartDate: r.StartDate,
        MaturityDate: r.MaturityDate,
        InterestVariant: r.InterestVariant,
        AutoRenewalPolicy: r.AutoRenewalPolicy,
        PaymentPeriodMonths: r.PaymentPeriodMonths,
        AccruedGrossInterestCents: r.AccruedGrossInterestCents,
        WithholdingToDateCents: r.WithholdingToDateCents,
        NetInterestCents: r.NetInterestCents,
        TotalPayoutCents: r.TotalPayoutCents,
        CouponsPaid: r.CouponsPaid,
        Lifecycle: r.Lifecycle,
        LastSequence: r.LastSequence,
        LastUpdated: r.LastUpdated);

    /// <summary>
    /// From an authoritative fold of the event stream — the read-your-writes fallback, served when the
    /// read-model row is missing or staler than the caller's <c>If-Min-Sequence</c> token. A folded
    /// engine deposit is always <c>sor = "engine"</c>; <see cref="LastSequence"/> is the folded head
    /// version and <see cref="LastUpdated"/> the last event's transaction_time — the SAME values the
    /// read-model row would carry. Only called when the stream is non-empty (Version >= 0), so
    /// <c>LastTransactionTime</c> is present (v1 has no snapshots — see <see cref="Hydrated{T}"/>).
    /// </summary>
    public static DepositResponse FromFold(Hydrated<DepositPosition> hydrated)
    {
        var p = hydrated.State;
        return new(
            DepositId: p.DepositId,
            Sor: "engine",
            PrincipalCents: p.Principal.Cents,
            TanBasisPoints: p.TanBasisPoints,
            RateSheetVersionId: p.RateSheetVersionId,
            ProductCode: p.ProductCode,
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
            Lifecycle: p.Lifecycle.ToString(),
            LastSequence: hydrated.Version,
            LastUpdated: hydrated.LastTransactionTime.GetValueOrDefault());
    }
}

/// <summary>The maturities range-query result (ADR-IC-005 <c>upcoming_maturities</c>): the deposits
/// maturing in the requested <c>[from, to)</c> window, ordered by maturity date.</summary>
public sealed record DepositMaturitiesResponse(IReadOnlyList<DepositResponse> Deposits);
