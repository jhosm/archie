using System.Text.Json;
using Npgsql;
using NpgsqlTypes;

namespace Babelstone.RateSheets;

/// <summary>
/// The PostgreSQL-backed <see cref="IRateSheetStore"/> (ADR-PC-008). Hand-rolled
/// against the <c>rate_sheets</c> table contract — no ORM (ADR-PC-010). Writes are
/// INSERT-only; immutability is enforced by the runtime role's privilege envelope
/// (migration 0004 / ADR-PC-001), not by this code. The body round-trips through
/// <see cref="RateSheetJson.Options"/> so the stored JSONB matches the deployed YAML.
/// </summary>
public sealed class PostgresRateSheetStore(string connectionString) : IRateSheetStore
{
    public async Task InsertAsync(RateSheet sheet, CancellationToken ct = default)
    {
        const string sql = """
            INSERT INTO rate_sheets (
                rate_sheet_version_id, product_family, pack_version, effective_from,
                body, approved_by, approval_ref, published_by)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8);
            """;

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.Add(new NpgsqlParameter { Value = sheet.RateSheetVersionId });
        command.Parameters.Add(new NpgsqlParameter { Value = sheet.ProductFamily });
        command.Parameters.Add(new NpgsqlParameter { Value = sheet.PackVersion });
        command.Parameters.Add(new NpgsqlParameter { Value = sheet.EffectiveFrom });
        command.Parameters.Add(new NpgsqlParameter
        {
            Value = JsonSerializer.Serialize(sheet.Body, RateSheetJson.Options),
            NpgsqlDbType = NpgsqlDbType.Jsonb,
        });
        command.Parameters.Add(new NpgsqlParameter { Value = sheet.ApprovedBy });
        command.Parameters.Add(new NpgsqlParameter { Value = sheet.ApprovalRef });
        command.Parameters.Add(new NpgsqlParameter { Value = sheet.PublishedBy });

        try
        {
            await command.ExecuteNonQueryAsync(ct);
        }
        catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            // Either the version-id PK or the (product_family, effective_from) unique key.
            // Surface a typed conflict; the deploy endpoint re-reads to decide idempotent
            // success vs a genuine 409 (ADR-PC-008).
            throw new DuplicateRateSheetVersionException(sheet.RateSheetVersionId, e);
        }
    }

    public async Task<RateSheet?> TryGetAsync(string rateSheetVersionId, CancellationToken ct = default)
    {
        const string sql = """
            SELECT rate_sheet_version_id, product_family, pack_version, effective_from,
                   body, approved_by, approval_ref, published_by, published_at
            FROM rate_sheets
            WHERE rate_sheet_version_id = @id;
            """;

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("id", rateSheetVersionId);

        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadSheet(reader) : null;
    }

    public async Task<RateSheetResolution?> ResolveAsync(
        string productFamily, DateTimeOffset asOf, CancellationToken ct = default)
    {
        // ADR-PC-008: "the sheet active at T" — highest effective_from not after the instant.
        // Served by rate_sheets_resolve_idx (product_family, effective_from DESC).
        const string sql = """
            SELECT rate_sheet_version_id, body
            FROM rate_sheets
            WHERE product_family = @family AND effective_from <= @as_of
            ORDER BY effective_from DESC
            LIMIT 1;
            """;

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("family", productFamily);
        command.Parameters.AddWithValue("as_of", asOf);

        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            return null;
        }

        return new RateSheetResolution(reader.GetString(0), DeserializeBody(reader, 1));
    }

    private static RateSheet ReadSheet(NpgsqlDataReader r) => new(
        RateSheetVersionId: r.GetString(0),
        ProductFamily: r.GetString(1),
        PackVersion: r.GetString(2),
        EffectiveFrom: r.GetFieldValue<DateTimeOffset>(3),
        Body: DeserializeBody(r, 4),
        ApprovedBy: r.GetString(5),
        ApprovalRef: r.GetString(6),
        PublishedBy: r.GetString(7),
        PublishedAt: r.GetFieldValue<DateTimeOffset>(8));

    private static RateSheetBody DeserializeBody(NpgsqlDataReader r, int ordinal) =>
        JsonSerializer.Deserialize<RateSheetBody>(r.GetFieldValue<string>(ordinal), RateSheetJson.Options)
            ?? throw new InvalidOperationException("Stored rate-sheet body deserialised to null.");
}
