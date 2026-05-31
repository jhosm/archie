namespace Babelstone.EventStore;

/// <summary>
/// A single Path-A bitemporal projection row (ADR-PC-002 §P1/§P2).
/// </summary>
/// <remarks>
/// <para>
/// The record carries two independent time axes (ADR-PC-002 §P1):
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
/// currently-believed projection (ADR-PC-002 §P2); a corrected row supersedes its
/// predecessor in place rather than deleting it, so the full belief history stays
/// queryable.
/// </item>
/// </list>
/// </para>
/// <para>
/// <see cref="StructuralPayload"/> is the serialized cleartext structural state — a
/// byte-oriented boundary mirroring the snapshot store. <see cref="PiiCiphertext"/> is
/// the ADR-PC-004 §P2 ciphertext envelope, carried as opaque bytes and left empty until
/// PII is added by a later task. No key material lives in this record.
/// </para>
/// </remarks>
public sealed record ProjectionRecord(
    Guid StreamId,
    DateTimeOffset ValidFrom,
    DateTimeOffset? ValidTo,
    DateTimeOffset RecordedAt,
    DateTimeOffset? SupersededAt,
    ReadOnlyMemory<byte> StructuralPayload,
    ReadOnlyMemory<byte> PiiCiphertext);
