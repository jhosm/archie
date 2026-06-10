using System.Text.Json;
using Babelstone.Engine;
using Babelstone.Packs;

namespace Babelstone.Engine.Api;

/// <summary>
/// The event-store payload codec: self-describing JSON. Per ADR-PC-028 this is the DECIDED, permanent
/// format for the <c>events.payload</c> book of record (readable with no
/// Schema Registry) — no longer the "deferred-Avro stand-in"; hardening it as the decided store codec
/// (deterministic order, explicit versioning) is bd babelstone-36mk. This same codec currently also fills
/// the OUTBOX payload, where it stays a placeholder until the Avro+SR bus encoding lands and the write
/// path dual-encodes (JSON → store, Avro+schema_id → outbox; ADR-IC-004 §P3). SchemaId is a constant 1 —
/// the placeholder for that outbound Avro id, not a decode key for the JSON payload.
/// </summary>
public sealed class JsonEventSerializer : IEventSerializer
{
    public EncodedPayload Encode(DomainEvent @event)
        => new(JsonSerializer.SerializeToUtf8Bytes(@event, @event.GetType()), SchemaId: 1);

    public DomainEvent Decode(ReadOnlyMemory<byte> payload, Type payloadType)
        => (DomainEvent)JsonSerializer.Deserialize(payload.Span, payloadType)!;
}

/// <summary>
/// The dev host's settlement adapter (ADR-PC-016 / ADR-PC-021 §D2): logs the money legs and
/// succeeds. The WireMock-backed SOAP stub is H.2; the real legacy ACL is DEF-1. A real adapter
/// signals a refused debit by throwing (see <see cref="ISettlementPort"/>); this dev stub never refuses.
/// </summary>
public sealed class LoggingSettlementPort(ILogger<LoggingSettlementPort> logger) : ISettlementPort
{
    public Task SettleAsync(SettlementInstruction instruction, CancellationToken ct = default)
    {
        logger.LogInformation(
            "settlement {Direction} {AmountCents}c on {Account} ({Reason}) for deposit {AggregateId}",
            instruction.Direction, instruction.Amount.Cents, instruction.Account, instruction.Reason,
            instruction.AggregateId);
        return Task.CompletedTask;
    }
}

/// <summary>
/// Loads the engine-instance's pinned regulatory pack off disk via a structural parse — the
/// walking-skeleton stand-in for the in-engine OCI loader (C.5 / <see cref="IPackStore"/>) and the
/// per-instance pinning registry (ADR-PC-009). Configure the packs directory with
/// <c>Engine:PacksDir</c>; otherwise it walks up from the host to find <c>packs/</c>.
/// </summary>
public static class HostPack
{
    private static readonly string[] DataFiles =
    [
        "pack.yaml",
        "primitives/day-count.yaml",
        "primitives/withholding.yaml",
        "primitives/fgd.yaml",
        "primitives/reporting.yaml",
        "parameters/constants.yaml",
        "rate-sheet-refs/deposits-pt.yaml",
    ];

    public static VerifiedPack Load(string? packsDir, string packVersion)
    {
        var root = packsDir ?? FindPacksDir();
        var packDir = Path.Combine(root, packVersion);
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var relativePath in DataFiles)
        {
            var diskPath = Path.Combine(packDir, relativePath.Replace('/', Path.DirectorySeparatorChar));
            files[relativePath] = File.ReadAllBytes(diskPath);
        }

        return PackParser.Parse(files, packVersion);
    }

    private static string FindPacksDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "packs")))
        {
            dir = dir.Parent;
        }

        return dir is not null
            ? Path.Combine(dir.FullName, "packs")
            : throw new InvalidOperationException(
                $"packs/ directory not found from {AppContext.BaseDirectory}; set Engine:PacksDir.");
    }
}
