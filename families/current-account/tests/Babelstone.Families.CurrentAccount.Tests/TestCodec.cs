using System.Text.Json;
using Babelstone.Engine;

namespace Babelstone.Families.CurrentAccount.Tests;

/// <summary>
/// A plain JSON event codec for tests. The engine test harness's equivalent lives in a different
/// assembly, so it is copied here rather than referenced (the same posture as the personal-loan
/// family's TestCodec). Used to satisfy any runner constructor that needs an
/// <see cref="IEventSerializer"/> — the pure-fold tests here never actually round-trip through it.
/// </summary>
public sealed class JsonEventSerializer : IEventSerializer
{
    public EncodedPayload Encode(DomainEvent @event)
        => new(JsonSerializer.SerializeToUtf8Bytes(@event, @event.GetType()), SchemaId: 1);

    public DomainEvent Decode(ReadOnlyMemory<byte> payload, Type payloadType)
        => (DomainEvent)JsonSerializer.Deserialize(payload.Span, payloadType)!;
}
