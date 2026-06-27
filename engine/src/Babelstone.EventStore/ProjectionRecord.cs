namespace Babelstone.EventStore;

/// <summary>
/// A single Path-A bitemporal projection row (ADR-PC-002).
/// </summary>
/// <remarks>
/// <para>
/// <see cref="ProjectionKind"/> is the family-prefixed discriminator (e.g.
/// <c>term_deposit.deposit_position</c>) — one stream carries more than one projection
/// — so supersession and current-belief reads scope to the
/// <c>(StreamId, ProjectionKind)</c> pair, and a PARTIAL UNIQUE index enforces exactly one
/// currently-believed row per pair (migration 0010).
/// </para>
/// <para>
/// <see cref="SourceSequence"/> is the per-stream <c>sequence_number</c> of the event that
/// produced this belief. The async drainer is at-least-once, so the apply step is made
/// idempotent by skipping any event whose <c>sequence_number</c> is <c>&lt;=</c> the current
/// belief's <see cref="SourceSequence"/> — without it the accumulating folds would
/// double-count a re-delivered event (migration 0010).
/// </para>
/// <para>
/// The record carries two independent time axes (ADR-PC-002):
/// <list type="bullet">
/// <item>
/// <b>World time</b> — <see cref="ValidFrom"/>/<see cref="ValidTo"/>: the slice of
/// believed-reality this row describes. A <see langword="null"/> <see cref="ValidTo"/>
/// means the world-time slice is open-ended (current and onward).
/// </item>
/// <item>
/// <b>Belief time</b> — <see cref="RecordedAt"/>/<see cref="SupersededAt"/>: when we
/// recorded this belief, and when (if ever) it was superseded by a forced correction.
/// A <see langword="null"/> <see cref="SupersededAt"/> means the row is the
/// currently-believed projection (ADR-PC-002); a corrected row supersedes its
/// predecessor in place rather than deleting it, so the full belief history stays
/// queryable. <see cref="RecordedAt"/> is RUNTIME-SUPPLIED (the source event's
/// transaction_time), never the SQL clock, so a cold rebuild reproduces it bit-for-bit
/// (ADR-PC-010; migration 0010 drops the column DEFAULT).
/// </item>
/// </list>
/// </para>
/// <para>
/// <see cref="StructuralPayload"/> is the serialized cleartext structural state — a
/// byte-oriented boundary mirroring the snapshot store. <see cref="PiiCiphertext"/> is
/// the ADR-PC-004 ciphertext envelope, carried as opaque bytes and left empty until
/// PII is added by a later task. No key material lives in this record.
/// </para>
/// </remarks>
public sealed record ProjectionRecord(
    Guid StreamId,
    string ProjectionKind,
    long SourceSequence,
    DateTimeOffset ValidFrom,
    DateTimeOffset? ValidTo,
    DateTimeOffset RecordedAt,
    DateTimeOffset? SupersededAt,
    ReadOnlyMemory<byte> StructuralPayload,
    ReadOnlyMemory<byte> PiiCiphertext);
