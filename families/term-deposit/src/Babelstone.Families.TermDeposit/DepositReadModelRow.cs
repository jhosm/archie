using Babelstone.EventStore;

namespace Babelstone.Families.TermDeposit;

/// <summary>
/// One denormalized CQRS read-model row for a term deposit (ADR-IC-005): the flat, query-optimized
/// read side that backs the sub-50ms client-facing query surface (the I.2 Query API), on the same
/// PostgreSQL tier as the event store (ADR-IC-005 §S1). This is the FAMILY-OWNED row shape — the
/// term-deposit family names its own typed query columns here, NOT the engine spine: the spine sees
/// it only through <see cref="IReadModelRow"/> (stream id + the §P2 sequence guard + the opaque
/// <see cref="Detail"/> body), so adding a non-deposit family is zero generic-engine diff
/// (ADR-PC-021 §D2/§P2). The matching <c>read_model.deposits</c> table and the maturity range scan
/// live in this family's <c>PostgresDepositReadModelStore</c> (the impure Application project), the
/// same split as <see cref="ProjectionRecord"/> / <c>IProjectionStorage</c> (generic spine) vs the
/// family's typed projection state (here).
/// </summary>
/// <remarks>
/// <para>
/// The row carries typed query columns (the denormalized dimensions the read API filters and
/// projects on) plus the opaque <see cref="Detail"/> payload — the serialized structural read body.
/// TWO product keys are surfaced under their honest names: <see cref="RateSheetVersionId"/> is the
/// PRICE/version key (one-to-many to products), and <see cref="ProductCode"/> is the catalogue
/// STRUCTURAL product code (e.g. <c>dpz_pt_12m_juros_venc</c>) — the queryable "which product is
/// this" dimension. Carrying the catalogue code onto <c>DepositConstituted</c>/the position is NOW
/// IMPLEMENTED (bd babelstone-v794, earlier deferred as the bd babelstone-yfr2 note). It is
/// PROSPECTIVE-ONLY: deposits constituted before v794 never carried it (the Avro field decodes to
/// the "" default) and the code is NOT back-fillable from the log — it was discarded at
/// constitution and the rate-sheet version is one-to-many to products — so historical rows carry
/// the empty code.
/// Keeping the body byte-oriented is what lets the read-model spine stay family-agnostic
/// (ADR-PC-021 §P2): the spine persists the opaque bytes + the §P2 sequence guard and never names a
/// deposit's body shape; the family owns the typed columns AND the <see cref="Detail"/>
/// serialization, the same split as <see cref="ProjectionRecord.StructuralPayload"/> over
/// <c>IProjectionStorage</c>.
/// </para>
/// <para>
/// <see cref="LastSequence"/> is the ADR-IC-005 §P2 monotonicity guard. This engine's event store
/// has no Redpanda offset (events drain per stream, no cluster-wide order), so the §P2
/// <c>last_event_offset</c> is realised as the per-stream <c>sequence_number</c> of the producing
/// event — a re-delivered or out-of-order event whose sequence is at or below the stored row's is
/// dropped by the UPSERT guard, making the at-least-once drainer safe.
/// <see cref="LastUpdated"/> (ADR-IC-005 §P3) is RUNTIME-SUPPLIED from the producing event's
/// transaction_time, never the SQL clock, so a cold rebuild (TRUNCATE + re-fold) reproduces the
/// row byte-for-byte (ADR-PC-010 §P5).
/// </para>
/// <para>
/// <see cref="Sor"/> is the ADR-PC-018 §6.2 routing-truth column: <c>engine</c> for every
/// engine-materialised deposit, <c>legacy</c> for an instance owned by the legacy core. The
/// channel/gateway tier READS it; the engine never embeds routing logic. No PII lives in this row
/// (ADR-PC-004 §P2) — structural deposit facts only.
/// </para>
/// </remarks>
public sealed record DepositReadModelRow(
    Guid StreamId,
    string Sor,
    long PrincipalCents,
    int TanBasisPoints,
    string RateSheetVersionId,
    string ProductCode,
    int TermDays,
    DateOnly StartDate,
    DateOnly MaturityDate,
    string InterestVariant,
    string Lifecycle,
    long TotalPayoutCents,
    ReadOnlyMemory<byte> Detail,
    long LastSequence,
    DateTimeOffset LastUpdated) : IReadModelRow;
