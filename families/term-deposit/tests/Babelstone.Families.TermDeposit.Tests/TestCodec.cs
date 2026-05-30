using System.Text.Json;
using Babelstone.Engine;

namespace Babelstone.Families.TermDeposit.Tests;

/// <summary>
/// A plain JSON event codec for tests (the Avro codec is deferred, skeleton §8). The engine
/// test harness's equivalent lives in a different assembly (Babelstone.Engine.Tests), so it
/// is copied here rather than referenced. Only used to satisfy the
/// <see cref="SimulationRuntime{TState}"/> constructor — <c>ProjectFromScratch</c> folds
/// in-memory and never actually encodes/decodes.
/// </summary>
public sealed class JsonEventSerializer : IEventSerializer
{
    public EncodedPayload Encode(DomainEvent @event)
        => new(JsonSerializer.SerializeToUtf8Bytes(@event, @event.GetType()), SchemaId: 1);

    public DomainEvent Decode(ReadOnlyMemory<byte> payload, Type payloadType)
        => (DomainEvent)JsonSerializer.Deserialize(payload.Span, payloadType)!;
}
