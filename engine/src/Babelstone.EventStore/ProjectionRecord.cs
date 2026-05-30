namespace Babelstone.EventStore;

/// <summary>
/// A persisted bitemporal projection row (ADR-PC-002 §P1/§P2) — the byte-oriented
/// storage shape. Two time axes are tracked:
/// <list type="bullet">
/// <item><description>
///   <b>World-time</b> — (<see cref="ValidFrom"/>, <see cref="ValidTo"/>): the real-world
///   interval during which the position was true. <see cref="ValidTo"/> <c>null</c> = open-ended.
/// </description></item>
/// <item><description>
///   <b>Transaction-time</b> — (<see cref="RecordedAt"/>, <see cref="SupersededAt"/>):
///   when the engine believed this row. <see cref="SupersededAt"/> <c>null</c> = currently-believed.
/// </description></item>
/// </list>
/// <para>
/// Structural columns are cleartext; PII is carried as an opaque BYTEA ciphertext
/// envelope (ADR-PC-004 §P2) resolved by the engine via OpenBao — this layer sees bytes only.
/// </para>
/// <para>
/// The typed, domain-aware wrapper lives in <c>Babelstone.Engine</c>; this record
/// stays Npgsql-only and domain-agnostic per the §3 dependency direction
/// (mirrors <see cref="SnapshotRecord"/>).
/// </para>
/// </summary>
/// <param name="StreamId">The deposit stream this row belongs to.</param>
/// <param name="ValidFrom">World-time start: when the position became real.</param>
/// <param name="ValidTo">World-time end; <c>null</c> = still open.</param>
/// <param name="RecordedAt">Transaction-time written — when the engine first believed this row.</param>
/// <param name="SupersededAt">Transaction-time corrected; <c>null</c> = currently-believed.</param>
/// <param name="StructuralPayload">Serialized structural (cleartext) projection state.</param>
/// <param name="PiiCiphertext">
///   Ciphertext envelope for PII fields (ADR-PC-004 §P2). Empty/default until PII is added
///   in later work.
/// </param>
public sealed record ProjectionRecord(
    Guid                 StreamId,
    DateTimeOffset       ValidFrom,
    DateTimeOffset?      ValidTo,
    DateTimeOffset       RecordedAt,
    DateTimeOffset?      SupersededAt,
    ReadOnlyMemory<byte> StructuralPayload,
    ReadOnlyMemory<byte> PiiCiphertext);
