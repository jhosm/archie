using System.Text.Json;
using System.Text.Json.Nodes;
using Npgsql;
using NpgsqlTypes;

namespace Babelstone.RateSheets;

/// <summary>
/// The PostgreSQL-backed <see cref="IProductConfigVersionStore"/> (ADR-PC-009 §A2, ADR-PC-008).
/// Hand-rolled against the <c>product_config_versions</c> table contract — no ORM (ADR-PC-010).
/// Writes are INSERT-only; immutability is enforced by the runtime role's privilege envelope
/// (migration 0021 / ADR-PC-001), not by this code. The body round-trips through
/// <see cref="ProductConfigJson.Options"/> so the stored JSONB matches the deployed YAML. Mirrors
/// <see cref="PostgresRateSheetStore"/> field-for-field so the two artefact families share one shape.
/// </summary>
public sealed class PostgresProductConfigVersionStore(string connectionString) : IProductConfigVersionStore
{
    public async Task InsertAsync(ProductConfigVersion version, CancellationToken ct = default)
    {
        const string sql = """
            INSERT INTO product_config_versions (
                product_config_version_id, product_id, pack_version, effective_from,
                body, content_hash, approved_by, approval_ref, published_by)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9);
            """;

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.Add(new NpgsqlParameter { Value = version.ProductConfigVersionId });
        command.Parameters.Add(new NpgsqlParameter { Value = version.ProductId });
        command.Parameters.Add(new NpgsqlParameter { Value = version.PackVersion });
        command.Parameters.Add(new NpgsqlParameter { Value = version.EffectiveFrom });
        command.Parameters.Add(new NpgsqlParameter
        {
            Value = version.Body.ToJsonString(ProductConfigJson.Options),
            NpgsqlDbType = NpgsqlDbType.Jsonb,
        });
        command.Parameters.Add(new NpgsqlParameter { Value = version.ContentHash });
        command.Parameters.Add(new NpgsqlParameter { Value = version.ApprovedBy });
        command.Parameters.Add(new NpgsqlParameter { Value = version.ApprovalRef });
        command.Parameters.Add(new NpgsqlParameter { Value = version.PublishedBy });

        try
        {
            await command.ExecuteNonQueryAsync(ct);
        }
        catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            // Either the version-id PK or the (product_id, effective_from) unique key. Surface a typed
            // conflict; the deploy endpoint re-reads to decide idempotent success vs a genuine 409
            // (ADR-PC-008 forward-only immutability).
            throw new DuplicateProductConfigVersionException(version.ProductConfigVersionId, e);
        }
    }

    public async Task<ProductConfigVersion?> TryGetAsync(
        string productConfigVersionId, CancellationToken ct = default)
    {
        const string sql = """
            SELECT product_config_version_id, product_id, pack_version, effective_from,
                   body, content_hash, approved_by, approval_ref, published_by, published_at
            FROM product_config_versions
            WHERE product_config_version_id = @id;
            """;

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("id", productConfigVersionId);

        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadVersion(reader) : null;
    }

    public async Task<ProductConfigVersionResolution?> ResolveAsync(
        string productId, DateTimeOffset asOf, CancellationToken ct = default)
    {
        // "The config version active at T" — highest effective_from not after the instant. Served by
        // product_config_versions_resolve_idx (product_id, effective_from DESC).
        const string sql = """
            SELECT product_config_version_id, content_hash, body
            FROM product_config_versions
            WHERE product_id = @product AND effective_from <= @as_of
            ORDER BY effective_from DESC
            LIMIT 1;
            """;

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("product", productId);
        command.Parameters.AddWithValue("as_of", asOf);

        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            return null;
        }

        return new ProductConfigVersionResolution(
            reader.GetString(0), reader.GetString(1), DeserializeBody(reader, 2));
    }

    private static ProductConfigVersion ReadVersion(NpgsqlDataReader r) => new(
        ProductConfigVersionId: r.GetString(0),
        ProductId: r.GetString(1),
        PackVersion: r.GetString(2),
        EffectiveFrom: r.GetFieldValue<DateTimeOffset>(3),
        Body: DeserializeBody(r, 4),
        ContentHash: r.GetString(5),
        ApprovedBy: r.GetString(6),
        ApprovalRef: r.GetString(7),
        PublishedBy: r.GetString(8),
        PublishedAt: r.GetFieldValue<DateTimeOffset>(9));

    private static JsonObject DeserializeBody(NpgsqlDataReader r, int ordinal) =>
        JsonNode.Parse(r.GetFieldValue<string>(ordinal)) as JsonObject
            ?? throw new InvalidOperationException("Stored product-config body deserialised to null or a non-object.");
}
