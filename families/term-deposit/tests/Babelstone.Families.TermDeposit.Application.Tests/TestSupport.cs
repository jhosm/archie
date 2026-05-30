using System.Text.Json;
using Babelstone.Engine;
using Babelstone.EventStore.Migrations;
using Babelstone.Packs;
using Babelstone.RateSheets;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace Babelstone.Families.TermDeposit.Application.Tests;

/// <summary>A plain JSON codec standing in for the deferred Avro codec (E.4); SchemaId is a constant 1.</summary>
internal sealed class JsonEventSerializer : IEventSerializer
{
    public EncodedPayload Encode(DomainEvent @event)
        => new(JsonSerializer.SerializeToUtf8Bytes(@event, @event.GetType()), SchemaId: 1);

    public DomainEvent Decode(ReadOnlyMemory<byte> payload, Type payloadType)
        => (DomainEvent)JsonSerializer.Deserialize(payload.Span, payloadType)!;
}

/// <summary>
/// The walking-skeleton settlement adapter (ADR-PC-021 §D2; ADR-PC-016): records the money legs
/// so the happy-path test can assert the debit + credit. The WireMock-backed SOAP stub is H.2;
/// the real ACL is DEF-1.
/// </summary>
internal sealed class RecordingSettlementPort : ISettlementPort
{
    private readonly List<SettlementInstruction> _instructions = [];

    public IReadOnlyList<SettlementInstruction> Instructions => _instructions;

    public Task SettleAsync(SettlementInstruction instruction, CancellationToken ct = default)
    {
        _instructions.Add(instruction);
        return Task.CompletedTask;
    }
}

/// <summary>Loads and parses the committed pt.2026.1 pack off disk (the C.5 structural parse, no oras/cosign).</summary>
internal static class SkeletonPack
{
    // The DATA_FILES a structural parse needs (pack.sh's list minus the sealed test-corpus).
    private static readonly string[] RelativePaths =
    [
        "pack.yaml",
        "primitives/day-count.yaml",
        "primitives/withholding.yaml",
        "primitives/fgd.yaml",
        "primitives/reporting.yaml",
        "parameters/constants.yaml",
        "rate-sheet-refs/deposits-pt.yaml",
    ];

    public static VerifiedPack LoadPt2026()
    {
        var packDir = Path.Combine(RepoRoot(), "packs", "pt.2026.1");
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var relativePath in RelativePaths)
        {
            var diskPath = Path.Combine(packDir, relativePath.Replace('/', Path.DirectorySeparatorChar));
            files[relativePath] = File.ReadAllBytes(diskPath);
        }

        return PackParser.Parse(files, "pt.2026.1");
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "packs", "pt.2026.1", "pack.yaml")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName
            ?? throw new InvalidOperationException(
                $"repo root (containing packs/pt.2026.1/pack.yaml) not found from {AppContext.BaseDirectory}");
    }
}

/// <summary>A rate sheet pricing one (product, role) at a flat TAN across all principals.</summary>
internal static class TestRateSheets
{
    public static RateSheet FlatPriced(
        string versionId, string productId, string role, int tanBasisPoints, DateTimeOffset effectiveFrom) =>
        new(
            RateSheetVersionId: versionId,
            ProductFamily: "term_deposit",
            PackVersion: "pt.2026.1",
            EffectiveFrom: effectiveFrom,
            Body: new RateSheetBody
            {
                Products = new Dictionary<string, Dictionary<string, RoleRates>>
                {
                    [productId] = new()
                    {
                        [role] = new RoleRates
                        {
                            Bands = [new RateBand { PrincipalCents = [0L, null], TanBasisPoints = tanBasisPoints }],
                        },
                    },
                },
            },
            ApprovedBy: "alm@bank.pt",
            ApprovalRef: "RC-2026-001",
            PublishedBy: "deploy@bank.pt");
}

/// <summary>PG18 with the engine migrations applied (events, outbox, snapshots, rate_sheets).</summary>
public sealed class ConstitutionFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _pg = new PostgreSqlBuilder("postgres:18-alpine").Build();

    public string ConnectionString => _pg.GetConnectionString();

    public async Task InitializeAsync()
    {
        await _pg.StartAsync();
        await new MigrationRunner(ConnectionString).ApplyAsync();
    }

    public async Task DisposeAsync() => await _pg.DisposeAsync();

    /// <summary>Counts rows whose <paramref name="idColumn"/> equals <paramref name="id"/> (events.stream_id / outbox.aggregate_id).</summary>
    public async Task<long> CountAsync(string table, string idColumn, Guid id)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand($"SELECT count(*) FROM {table} WHERE {idColumn} = @id;", connection);
        command.Parameters.AddWithValue("id", id);
        return (long)(await command.ExecuteScalarAsync())!;
    }
}
