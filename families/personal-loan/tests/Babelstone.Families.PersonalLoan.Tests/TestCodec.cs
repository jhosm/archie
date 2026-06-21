using System.Text.Json;
using Babelstone.Engine;

namespace Babelstone.Families.PersonalLoan.Tests;

/// <summary>
/// A plain JSON event codec for tests. The engine test harness's equivalent lives in a different
/// assembly, so it is copied here rather than referenced. Used to satisfy the projection runner's
/// constructor — the in-memory fold path never actually round-trips through it for the pure-fold tests,
/// and where it does (the runner tests) JSON is sufficient.
/// </summary>
public sealed class JsonEventSerializer : IEventSerializer
{
    public EncodedPayload Encode(DomainEvent @event)
        => new(JsonSerializer.SerializeToUtf8Bytes(@event, @event.GetType()), SchemaId: 1);

    public DomainEvent Decode(ReadOnlyMemory<byte> payload, Type payloadType)
        => (DomainEvent)JsonSerializer.Deserialize(payload.Span, payloadType)!;
}
