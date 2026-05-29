namespace Babelstone.Engine;

/// <summary>An encoded event payload plus the schema-registry id embedded at write time (ADR-IC-004 §P3).</summary>
public sealed record EncodedPayload(byte[] Bytes, int SchemaId);

/// <summary>
/// Serializes domain events to/from payload bytes. The seam that hides which Avro
/// library lands (skeleton §8 / ADR-IC-002) — the pick is tied to the schema-registry
/// integration Epic E brings online. Tests supply a simple codec.
/// </summary>
public interface IEventSerializer
{
    EncodedPayload Encode(DomainEvent @event);
    DomainEvent Decode(ReadOnlyMemory<byte> payload, Type payloadType);
}

/// <summary>
/// Applies the field-level PII envelope (ADR-PC-004 §P2) to an event before it is
/// serialized, and reverses it on load. This is the §5.3 encrypt seam — the single
/// place OpenBao is reached on the write path.
/// </summary>
/// <remarks>
/// Which fields are PII is declared by the family's CUE schema (ADR-PC-004 §P1); until
/// that annotation source exists (Epic C, tracked as archie-e6fr.5) the engine ships
/// <see cref="NullPiiProtector"/>. The seam (and the <c>Babelstone.Pii</c> dependency
/// it fronts) is in place so the real protector drops in without touching the runtime.
/// </remarks>
public interface IPiiProtector
{
    Task<DomainEvent> ProtectAsync(DomainEvent @event, CancellationToken ct = default);
    Task<DomainEvent> UnprotectAsync(DomainEvent @event, CancellationToken ct = default);
}

/// <summary>Identity protector: no annotated fields known yet, so nothing to encrypt (see <see cref="IPiiProtector"/> remarks).</summary>
public sealed class NullPiiProtector : IPiiProtector
{
    public Task<DomainEvent> ProtectAsync(DomainEvent @event, CancellationToken ct = default) => Task.FromResult(@event);

    public Task<DomainEvent> UnprotectAsync(DomainEvent @event, CancellationToken ct = default) => Task.FromResult(@event);
}
