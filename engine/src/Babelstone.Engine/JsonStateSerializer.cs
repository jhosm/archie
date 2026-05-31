using System.Text.Json;

namespace Babelstone.Engine;

/// <summary>
/// Serializes projection / snapshot state to bytes via System.Text.Json with default options.
/// Property order is the type's declaration order, so output is deterministic for a given type
/// — the byte-identity the rebuild-determinism gate (ADR-PC-010 §P5) and the snapshot hash rely
/// on. Families and the host use this for the structural payload; no PII rides here (structural
/// state only, ADR-PC-004 §P2).
/// </summary>
public sealed class JsonStateSerializer<TState> : IStateSerializer<TState>
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.General);

    public byte[] Serialize(TState state) => JsonSerializer.SerializeToUtf8Bytes(state, Options);

    public TState Deserialize(ReadOnlyMemory<byte> bytes) =>
        JsonSerializer.Deserialize<TState>(bytes.Span, Options)
        ?? throw new InvalidOperationException($"Deserialized null state for {typeof(TState).Name}.");
}
