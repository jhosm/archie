using Babelstone.Engine;
using Babelstone.EventStore;
using Babelstone.Families.TermDeposit;

namespace Babelstone.Families.TermDeposit.Application;

// The deposits HTTP contract (ADR-PC-021 §D5 boundary). snake_case on the wire (the host's
// JSON options), money as integer cents — never a nested object or a float (ADR-PC-010 §P1).

/// <summary>
/// Constitute a deposit. Per-deposit facts only; the TAN is resolved from the rate sheet, not supplied.
/// </summary>
/// <remarks>
/// <para>
/// <b>Structural facts are OPTIONAL — the engine resolves them from the product code (Fork B rework,
/// bd t7o3.11 / 3k10 / c8d8, ADR-PC-009).</b> The saga now sends only the MINIMAL body —
/// <c>product_id</c>, <c>principal_cents</c>, <c>funding_account</c>, <c>deposit_id</c> — and the engine
/// looks up the term / interest variant / renewal policy / coupon cadence / pricing role from its
/// deployed <c>product-configs/</c> store at constitution. So the orchestrator carries NO product-family
/// knowledge; the engine is the single home of product config. The structural fields stay nullable so
/// direct callers that DO know the shape (the MCP agent, API tests) may still supply them — when
/// present they are honoured; when absent the engine resolves them. The start date is derived host-side
/// from <c>constituted_at</c> (the engine is the constitution authority); the role defaults to the
/// product config's default when omitted.
/// </para>
/// </remarks>
/// <param name="Role">Optional pricing-role override; resolved from the product config (v1: <c>standard</c>) when null.</param>
/// <param name="TermDays">Optional; resolved from the product config when null.</param>
/// <param name="StartDate">Optional; host-stamped from <c>constituted_at</c> when null.</param>
/// <param name="InterestVariant">Optional; resolved from the product config when null.</param>
/// <param name="AutoRenewalPolicy">Optional; resolved from the product config when null.</param>
/// <param name="PaymentPeriodMonths">PERIODIC coupon cadence in months (1 or 3); omit/0 for
/// AT_MATURITY and ADVANCE. When the structural facts are resolved engine-side this is taken from the
/// product config; a supplied value is honoured on the full-facts path.</param>
public sealed record ConstituteDepositRequest(
    long PrincipalCents,
    string ProductId,
    string FundingAccount,
    string? Role = null,
    int? TermDays = null,
    DateOnly? StartDate = null,
    string? InterestVariant = null,
    string? AutoRenewalPolicy = null,
    Guid? DepositId = null,
    DateTimeOffset? ConstitutedAt = null,
    string? Actor = null,
    int? PaymentPeriodMonths = null);

/// <summary>
/// The constitution outcome — the assigned id, lifecycle state (synchronous in the walking skeleton),
/// and <c>CommitSequence</c>: the per-stream version the append reached (ADR-IC-005 §P3). A caller
/// hands this straight back as the <c>If-Min-Sequence</c> token on the follow-up
/// <c>GET /v1/deposits/{id}</c> to get read-your-writes — the engine folds the stream rather than
/// serving a read-model row the projector has not yet caught up to.
/// </summary>
public sealed record ConstituteDepositResponse(Guid DepositId, string Status, long CommitSequence);

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

/// <summary>Withdraw part of a deposit's principal before maturity (F.12; 02 §2.4.1): a principal
/// reduction that leaves the deposit Active. The product's policy (minimum withdrawal / minimum
/// remaining balance / lock-up) is resolved ENGINE-side from the deposit's product config —
/// not supplied here. The instant is host-stamped if omitted; its DATE is the as-of withdrawal date the
/// lock-up is measured against. No payout account: a partial withdrawal carries no settlement leg.</summary>
/// <param name="WithdrawnAmountCents">The principal to take out, in integer cents.</param>
public sealed record PartialWithdrawRequest(
    long WithdrawnAmountCents,
    DateTimeOffset? WithdrawnAt = null,
    string? Actor = null);

/// <summary>
/// Break a constituted deposit before maturity (02 §2.5; the F.4 early-termination money-mover endpoint,
/// bd babelstone-t7o3.13.1). The engine accrues the elapsed-period interest, withholds it, applies the
/// product's configured penalty (resolved ENGINE-side from the product config — not supplied here), and
/// records the NET settlement payout APPEND-FIRST as an Originated Credit <see cref="Babelstone.Engine.Movement"/>
/// on <c>DepositTerminatedEarly</c>, which the substrate-owned settlement saga effects as the gated ACL
/// credit. The instant is host-stamped if omitted; its DATE is the as-of termination date the elapsed
/// interest accrues to and the penalty band is selected against.
/// </summary>
/// <param name="PayoutAccount">The opaque current-account token the net settlement is credited to — a
/// reference, NEVER an IBAN (ADR-PC-004 §P2). Defaults to the dev current account when omitted.</param>
/// <param name="TerminationReason">A stable, non-PII reason code recorded on the event (e.g.
/// <c>CUSTOMER_REQUEST</c>) — never anything about the customer (ADR-PC-004 §P2). Defaults to
/// <c>CUSTOMER_REQUEST</c>.</param>
/// <param name="TerminatedAt">The break instant; host-stamped if omitted.</param>
/// <param name="Actor">The acting principal recorded on the append; defaults to <c>mcp:dev</c>.</param>
public sealed record TerminateEarlyRequest(
    string? PayoutAccount = null,
    string? TerminationReason = null,
    DateTimeOffset? TerminatedAt = null,
    string? Actor = null);

/// <summary>The early-termination outcome: the deposit, its terminal <c>TERMINATEDEARLY</c> status, and the
/// commit sequence the termination append reached (ADR-IC-005 §P3 read-your-writes token). Carries no PII —
/// structural facts only.</summary>
public sealed record TerminateEarlyResponse(Guid DepositId, string Status, long CommitSequence);

/// <summary>
/// Exercise the data subject's GDPR Article 17 right-to-be-forgotten on a deposit (bd babelstone-nzw6):
/// the engine crypto-shreds the subject's encryption key (ADR-PC-004 §P3) and records the erasure fact.
/// </summary>
/// <remarks>
/// <paramref name="SubjectId"/> is the raw data-subject key name — the ONE place it appears, in the
/// request body of an authenticated erasure command (the caller is e.g. a compliance officer acting on a
/// verified request). It is used at the host ONLY to (a) destroy the subject's key and (b) derive the
/// salted one-way pseudonym that goes on the persisted event; it is NEVER stored on the bus or a span
/// (ADR-PC-004 §P2). The host holds the pseudonym salt as a secret (ISecretProvider), not the request.
/// </remarks>
/// <param name="SubjectId">The data-subject key name to crypto-shred. Resolved at the host, never persisted.</param>
/// <param name="ErasureReason">A stable machine code for the erasure (defaults to <c>GDPR_ARTICLE_17</c>) — never PII.</param>
/// <param name="ErasedAt">The erasure instant; host-stamped if omitted.</param>
/// <param name="Actor">The acting principal recorded on the append (defaults to <c>gdpr:erasure</c>).</param>
public sealed record ErasePersonalDataRequest(
    string SubjectId,
    string? ErasureReason = null,
    DateTimeOffset? ErasedAt = null,
    string? Actor = null);

/// <summary>The erasure outcome: the deposit, its terminal <c>ERASED</c> status, and the commit
/// sequence of the appended audit fact. Carries no PII — the subject id is never echoed back.</summary>
public sealed record ErasePersonalDataResponse(Guid DepositId, string Status, long CommitSequence);

/// <summary>
/// Correct a previously-recorded fact on a live deposit (D.5 / F.6, bd babelstone-k6r8.11): the
/// operator-only HTTP front door to the bitemporal supersession (ADR-PC-002 §P2) the projection runtime
/// already implements. The route <c>{id}</c> is the deposit id; the body carries the correction's
/// structural facts. The engine appends a single store-only <c>DepositCorrected</c> whose valid-time is
/// <see cref="EffectiveFrom"/>, which the bitemporal projection turns into a supersede-then-insert — the
/// prior belief is kept and disavowed, never overwritten. STORE-ONLY: no money moves, the deposit stays
/// Active. The endpoint requires an OPERATOR actor (the <c>ops:</c> / <c>operator:</c> namespace) and a
/// mandatory <c>Idempotency-Key</c> (ADR-PC-029 slot 4 — a correction is repeatable, so a retry must
/// dedupe rather than double-tally).
/// </summary>
/// <remarks>
/// Every field is a structural value or a stable code — NO PII (ADR-PC-004 §P2). The body carries the
/// corrected VALUE inline as a typed structural field (bd babelstone-j7mm.2): the field
/// <see cref="CorrectedField"/> names carries its corrected value (principal as integer cents, rate as
/// basis points, dates), the fold substitutes it, and a query reads back the corrected value.
/// </remarks>
/// <param name="CorrectionId">A stable, operator-supplied id for this correction (e.g. <c>corr-001</c>),
/// recorded for audit lineage. Distinct from the <c>Idempotency-Key</c> (the ADR-PC-029 dedup key).</param>
/// <param name="CorrectedField">The structural field being corrected (e.g. <c>principal</c>, <c>rate</c>,
/// <c>start_date</c>, <c>maturity_date</c>) — a stable name, never a value. An unknown / non-correctable
/// field is rejected with a 422 before any append.</param>
/// <param name="CorrectedPrincipalCents">The corrected principal in integer cents when
/// <see cref="CorrectedField"/> is <c>principal</c>; null otherwise (money is cents on the wire, never a
/// float — ADR-PC-010 §P1).</param>
/// <param name="CorrectedTanBasisPoints">The corrected nominal annual rate in basis points when
/// <see cref="CorrectedField"/> is <c>rate</c>; null otherwise.</param>
/// <param name="CorrectedStartDate">The corrected start value-date when <see cref="CorrectedField"/> is
/// <c>start_date</c>; null otherwise.</param>
/// <param name="CorrectedMaturityDate">The corrected maturity date when <see cref="CorrectedField"/> is
/// <c>maturity_date</c>; null otherwise.</param>
/// <param name="EffectiveFrom">The valid-time the correction takes effect FROM — the date that feeds the
/// ADR-PC-002 §P2 bitemporal supersession (the append's <c>ValidTime</c> at midnight UTC).</param>
/// <param name="CorrectionReason">A stable, non-PII reason code/string (e.g. <c>clerk-entry</c>).</param>
/// <param name="Actor">The OPERATOR actor recorded on the append (e.g. <c>ops:clerk</c>); a non-operator
/// actor is rejected with a 422. Defaults to <c>ops:clerk</c> when omitted.</param>
public sealed record CorrectDepositRequest(
    string CorrectionId,
    string CorrectedField,
    long? CorrectedPrincipalCents,
    int? CorrectedTanBasisPoints,
    DateOnly? CorrectedStartDate,
    DateOnly? CorrectedMaturityDate,
    DateOnly EffectiveFrom,
    string CorrectionReason,
    string? Actor = null);

/// <summary>The correction outcome: the deposit, its (unchanged, still Active) lifecycle status, and the
/// commit sequence the correction append reached (ADR-IC-005 §P3 read-your-writes token). Carries no PII —
/// structural facts only.</summary>
public sealed record CorrectDepositResponse(Guid DepositId, string Status, long CommitSequence);

/// <summary>
/// Step 2 of the renewal saga (bd babelstone-mtto PR B): open the renewed instance off a CLOSING
/// (Matured) deposit. The route <c>{id}</c> is the closing deposit id; the body is MINIMAL
/// (bd babelstone-mtto.5) — just the new deposit id, the renewal instant and the actor. The engine
/// reads EVERY renewal fact — the term / variant / cadence / policy AND the product code, pricing role
/// and funding-account token — off the (Matured) closing deposit's folded state, now that
/// <c>DepositConstituted</c> persists role + funding alongside the already-persisted product code. So
/// the orchestrator carries NO product-family knowledge (ADR-IC-003 §A7): the engine resolves product
/// facts in-tx from the closing deposit it already loads. The instant is host-stamped if omitted.
/// </summary>
/// <param name="NewDepositId">The fresh stream id the renewed instance is constituted under (the saga
/// derives it deterministically, so the renewal is a replayable command).</param>
/// <param name="RenewedAt">The renewal instant — the new sheet is resolved as-of here and it is the new
/// constitution's valid time (its DATE is the renewal/new-start date). Host-stamped if omitted.</param>
/// <param name="Actor">The acting principal recorded on the new stream's append (defaults to <c>saga:renewal</c>).</param>
public sealed record ConstituteRenewalRequest(
    Guid NewDepositId,
    DateTimeOffset? RenewedAt = null,
    string? Actor = null);

/// <summary>The constitute-renewal outcome: the closing deposit id, the NEW (renewed) deposit id, its
/// lifecycle, and the new stream's <c>CommitSequence</c> (ADR-IC-005 §P3 read-your-writes token).</summary>
public sealed record ConstituteRenewalResponse(
    Guid DepositId, Guid NewDepositId, string Status, long CommitSequence);

/// <summary>
/// Step 3 of the renewal saga (bd babelstone-mtto PR B): link the renewal, folding the CLOSING stream
/// Matured → Renewed. The route <c>{id}</c> is the closing deposit id; the body carries the new deposit
/// id whose head DepositConstituted fills the <c>DepositRenewed</c> link. The instant is host-stamped if
/// omitted.
/// </summary>
/// <param name="NewDepositId">The renewed instance's stream id (opened by constitute-renewal).</param>
/// <param name="RenewedAt">The valid time recorded on the <c>DepositRenewed</c> append. Host-stamped if omitted.</param>
/// <param name="Actor">The acting principal recorded on the closing stream's append (defaults to <c>saga:renewal</c>).</param>
public sealed record LinkRenewalRequest(
    Guid NewDepositId,
    DateTimeOffset? RenewedAt = null,
    string? Actor = null);

/// <summary>The renewal-link outcome: the closing deposit id, the new deposit id, the closing deposit's
/// terminal lifecycle (RENEWED), and the closing stream's post-link <c>CommitSequence</c>.</summary>
public sealed record LinkRenewalResponse(
    Guid DepositId, Guid NewDepositId, string Status, long CommitSequence);

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
