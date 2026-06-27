namespace Babelstone.Engine;

/// <summary>An encoded event payload plus the codec's schema id embedded at write time — a registered
/// Schema-Registry id on the bus path, the self-describing store-codec id on the store path (ADR-IC-004 / ADR-PC-028).</summary>
public sealed record EncodedPayload(byte[] Bytes, int SchemaId);

/// <summary>
/// Encodes/decodes a domain event to/from payload bytes — the kernel's single codec seam, used in two
/// roles by the host: the registry-free self-describing JSON STORE codec for <c>events.payload</c>, and
/// the Avro + Schema-Registry BUS codec for <c>outbox.payload</c>. Both describe the same event
/// (ADR-PC-028 / STORE_BUS_ENCODING_EQUIVALENCE); the kernel names neither Avro nor the Schema Registry.
/// Tests supply a simple codec.
/// </summary>
public interface IEventSerializer
{
    EncodedPayload Encode(DomainEvent @event);
    DomainEvent Decode(ReadOnlyMemory<byte> payload, Type payloadType);
}

/// <summary>
/// Applies the field-level PII envelope (ADR-PC-004) to an event before it is serialized, and
/// reverses it on load — the encrypt seam, the single place OpenBao is reached on the write path.
/// </summary>
/// <remarks>
/// Which fields are PII is declared by the family's CUE schema (ADR-PC-004); until that annotation
/// source exists the engine ships <see cref="NullPiiProtector"/>.
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
