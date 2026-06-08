namespace Babelstone.EventStore;

/// <summary>
/// One denormalized CQRS read-model row for a deposit (ADR-IC-005): the flat, query-optimized
/// read side that backs the sub-50ms client-facing query surface (the I.2 Query API), on the same
/// PostgreSQL tier as the event store (ADR-IC-005 §S1). This is DISTINCT from
/// <see cref="ProjectionRecord"/>: that is the bitemporal belief store (ADR-PC-002, the typed
/// AsOf/CurrentBelief/HistoryOf query); this is the flat read model whose columns are the query
/// dimensions ADR-IC-005 names (point lookup by id, range scan by <see cref="MaturityDate"/>).
/// </summary>
/// <remarks>
/// <para>
/// The row carries typed query columns (the denormalized dimensions the read API filters and
/// projects on) plus an opaque <see cref="Detail"/> payload — the serialized structural read body.
/// The product KEY is <see cref="RateSheetVersionId"/>; there is deliberately no catalogue
/// <c>product_id</c> column. The catalogue product code (e.g. <c>dpz_pt_12m_juros_venc</c>) is
/// resolved into the TAN + rate-sheet version at constitution and does not survive onto
/// <c>DepositConstituted</c>/the position, so a <c>product_id</c> column populated from the
/// position would merely duplicate the version id under a misleading name (a client filtering on
/// the catalogue code would match nothing). Carrying the catalogue code onto the event is a
/// separable follow-up (bd babelstone-yfr2 deferred note).
/// Keeping the body byte-oriented is what lets the read-model STORE stay family-agnostic
/// (ADR-PC-021 §P2): the spine persists the typed columns + opaque bytes and never names a
/// deposit's body shape; the family owns the <see cref="Detail"/> serialization, the same split as
/// <see cref="ProjectionRecord.StructuralPayload"/> over <see cref="IProjectionStorage"/>.
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
public sealed record ReadModelRow(
    Guid StreamId,
    string Sor,
    long PrincipalCents,
    int TanBasisPoints,
    string RateSheetVersionId,
    int TermDays,
    DateOnly StartDate,
    DateOnly MaturityDate,
    string InterestVariant,
    string Lifecycle,
    long TotalPayoutCents,
    ReadOnlyMemory<byte> Detail,
    long LastSequence,
    DateTimeOffset LastUpdated);
