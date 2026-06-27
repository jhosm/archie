namespace Babelstone.EventStore;

/// <summary>
/// The generic, family-agnostic contract a denormalized CQRS read-model row (ADR-IC-005) exposes
/// to the spine. The spine knows ONLY these three columns; a family supplies the concrete row type
/// carrying its own typed query dimensions, the same split as <see cref="ProjectionRecord"/> keeps
/// the bitemporal store generic (generic axes + opaque <see cref="ProjectionRecord.StructuralPayload"/>,
/// with the family's typed shape living in the family layer). This is what keeps
/// <c>Babelstone.EventStore</c>/<c>Babelstone.Engine</c> under ENGINE_FAMILY_AGNOSTIC
/// (ADR-PC-021): the spine never names a deposit's body shape or a deposit-specific query
/// column — adding a non-deposit family is zero generic-engine diff.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="StreamId"/> is the row key (= the aggregate/stream id). <see cref="LastSequence"/> is
/// the ADR-IC-005 monotonicity guard the spine's UPSERT and the runner's skip both read — this
/// engine's event store has no Redpanda offset (events drain per stream, no cluster-wide order), so
/// the §P2 <c>last_event_offset</c> is realised as the per-stream <c>sequence_number</c> of the
/// producing event. <see cref="Detail"/> is the serialized structural read body, carried as opaque
/// bytes so the spine store and the generic <c>Babelstone.Engine</c> runner stay family-blind
/// (the family owns the serialization, the same boundary as
/// <see cref="ProjectionRecord.StructuralPayload"/> over <see cref="IProjectionStorage"/>); the
/// runner re-hydrates it to continue an accumulating fold across events.
/// </para>
/// <para>
/// No PII lives on a read-model row (ADR-PC-004) — structural facts only. PII, when it lands,
/// rides a separate ciphertext envelope, never the durable read surface.
/// </para>
/// </remarks>
public interface IReadModelRow
{
    /// <summary>The row key — the aggregate/stream id this denormalized row materialises.</summary>
    Guid StreamId { get; }

    /// <summary>
    /// The ADR-IC-005 monotonicity guard: the per-stream <c>sequence_number</c> of the event
    /// that produced this row's state. The spine UPSERT overwrites only on a strictly greater value,
    /// so an at-least-once re-delivery is a no-op.
    /// </summary>
    long LastSequence { get; }

    /// <summary>
    /// The opaque serialized structural read body. The spine and the generic read-model runner treat
    /// it as bytes; the family owns its shape and codec. The runner re-hydrates it to continue an
    /// accumulating fold from where the last event left off.
    /// </summary>
    ReadOnlyMemory<byte> Detail { get; }
}
