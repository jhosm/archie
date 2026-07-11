using System.Text.Json;
using Babelstone.Engine;
using Babelstone.EventStore.Migrations;
using Babelstone.Packs;
using Babelstone.RateSheets;
using Babelstone.TestFixtures;
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
        "families.yaml",
        "rate-sheet-refs/deposits-pt.yaml",
        "rate-sheet-refs/current-account-pt.yaml",
        "rate-sheet-refs/loans-pt.yaml",
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
        MultiPriced(versionId, effectiveFrom, (productId, role, tanBasisPoints));

    /// <summary>
    /// A rate sheet pricing SEVERAL (product, role) pairs at flat TANs in one sheet. The engine's
    /// rate-sheet resolution (<see cref="PostgresRateSheetStore.ResolveAsync"/>) picks the latest sheet
    /// effective for the FAMILY at the instant — it does not filter by product — so when several
    /// products coexist in one Postgres (the shared class fixture), they must live in ONE sheet, or a
    /// deposit can resolve a sheet that does not price its product. This builds that one sheet.
    /// </summary>
    public static RateSheet MultiPriced(
        string versionId, DateTimeOffset effectiveFrom,
        params (string ProductId, string Role, int TanBasisPoints)[] pricings)
    {
        var products = new Dictionary<string, Dictionary<string, RoleRates>>();
        foreach (var (productId, role, tanBasisPoints) in pricings)
        {
            if (!products.TryGetValue(productId, out var roles))
            {
                roles = new Dictionary<string, RoleRates>();
                products[productId] = roles;
            }

            roles[role] = new RoleRates
            {
                Bands = [new RateBand(0L, null, tanBasisPoints)],
            };
        }

        return new RateSheet(
            RateSheetVersionId: versionId,
            ProductFamily: "term_deposit",
            PackVersion: "pt.2026.1",
            EffectiveFrom: effectiveFrom,
            Body: new RateSheetBody { Products = products },
            ApprovedBy: "alm@bank.pt",
            ApprovalRef: "RC-2026-001",
            PublishedBy: "deploy@bank.pt");
    }
}

/// <summary>
/// PG18 with the engine migrations applied (events, outbox, snapshots, rate_sheets, projections)
/// AND the term-deposit family's own read-model migration (read_model.deposits). The read model is
/// family-owned now (ADR-PC-021 family-owned ownership), so it ships in a SEPARATE migration set on
/// the same tier (ADR-IC-005 §S1); the fixture applies ENGINE FIRST then FAMILY, the hard ordering
/// the family migration's fail-loud role guard depends on.
/// </summary>
public sealed class ConstitutionFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _pg = new PostgreSqlBuilder("postgres:18-alpine").Build();

    public string ConnectionString => _pg.GetConnectionString();

    public async Task InitializeAsync()
    {
        await _pg.GatedStartAsync();
        // Engine event-store schema first (it creates the babelstone_engine role the family read
        // model GRANTs on), then the family read-model schema — engine-before-family ordering.
        await new MigrationRunner(ConnectionString).ApplyAsync();
        await new Babelstone.Families.TermDeposit.Application.Migrations.MigrationRunner(ConnectionString).ApplyAsync();
    }

    public async Task DisposeAsync() => await _pg.DisposeAsync();

    /// <summary>
    /// Insert a rate sheet once, ignoring a duplicate. The three Integration tests share this one
    /// Postgres container and the engine resolves the latest sheet effective for the FAMILY (not by
    /// product), so they must share ONE sheet that prices every product they use; whichever test runs
    /// first inserts it, the rest no-op on the (product_family, effective_from) unique key.
    /// </summary>
    public async Task EnsureRateSheetAsync(RateSheet sheet)
    {
        try
        {
            await new PostgresRateSheetStore(ConnectionString).InsertAsync(sheet);
        }
        catch (DuplicateRateSheetVersionException)
        {
            // Another test already inserted the shared sheet — idempotent.
        }
    }

    /// <summary>Counts rows whose <paramref name="idColumn"/> equals <paramref name="id"/> (events.stream_id / outbox.aggregate_id).</summary>
    public async Task<long> CountAsync(string table, string idColumn, Guid id)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand($"SELECT count(*) FROM {table} WHERE {idColumn} = @id;", connection);
        command.Parameters.AddWithValue("id", id);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    /// <summary>The <c>event_id</c> of the single event of <paramref name="eventType"/> on the
    /// <paramref name="streamId"/> stream (e.g. <c>term_deposit.DepositMatured</c>) — for asserting the
    /// renewal's causation root. Throws if absent or non-unique.</summary>
    public async Task<Guid> EventIdAsync(Guid streamId, string eventType)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT event_id FROM events WHERE stream_id = @id AND event_type = @type;", connection);
        command.Parameters.AddWithValue("id", streamId);
        command.Parameters.AddWithValue("type", eventType);
        return (Guid)(await command.ExecuteScalarAsync()
            ?? throw new InvalidOperationException($"no {eventType} on stream {streamId}"));
    }

    /// <summary>The <c>causation_id</c> of the first event on the <paramref name="streamId"/> stream
    /// (sequence 0) — for asserting a renewed instance's <c>DepositConstituted</c> roots at the closing
    /// <c>DepositMatured</c>. Null when the column is null.</summary>
    public async Task<Guid?> FirstEventCausationIdAsync(Guid streamId)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT causation_id FROM events WHERE stream_id = @id AND sequence_number = 0;", connection);
        command.Parameters.AddWithValue("id", streamId);
        var value = await command.ExecuteScalarAsync();
        return value is DBNull or null ? null : (Guid)value;
    }
}
