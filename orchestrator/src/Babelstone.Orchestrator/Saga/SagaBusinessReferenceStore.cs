using Babelstone.Orchestrator.Handlers;
using Npgsql;

namespace Babelstone.Orchestrator.Saga;

/// <summary>
/// The persistence boundary for the per-saga <see cref="SagaBusinessReference"/> side table
/// (<c>saga_business_ref</c>, migration 0006). Hand-rolled Npgsql (ADR-PC-010): no ORM, no framework
/// owns the table. The edge writes the row ONCE at start (in the saga transaction, so it commits
/// atomically with the STARTED row); the approval fork and the command-payload assembly only read it.
/// Every column is a structural reference or an integer-cents scalar — NO PII (ADR-PC-004 §P2).
/// </summary>
public sealed class SagaBusinessReferenceStore
{
    /// <summary>
    /// INSERT the saga's pinned business references on the caller's transaction (idempotent on the
    /// process id: a duplicate start collides on <c>saga_business_ref_pkey</c> and is a no-op, so a
    /// redelivered/colliding start never rewrites the pinned facts). The closed
    /// <see cref="ClientType"/> persists as its SCREAMING_SNAKE code; the amounts are integer cents.
    /// </summary>
    /// <returns>True if this call wrote the row; false if it already existed.</returns>
    public async Task<bool> TryInsertAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        SagaBusinessReference reference,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(reference);

        // The 0006 shape ONLY (Fork B rework, bd t7o3.11 / 3k10 / c8d8): the orchestrator carries NO
        // product-family knowledge, so the structural product facts the rejected v1 stand-in stored here
        // (migration 0007: term_days / interest_variant / auto_renewal_policy / payment_period_months /
        // role / start_date) are GONE — the engine resolves them from the product code at constitution
        // (ADR-PC-009). product_ref is the product code; the engine looks the shape up.
        const string sql = """
            INSERT INTO saga_business_ref (
                process_id, product_ref, amount_minor_units, source_account_ref,
                interest_account_ref, deposit_ref, client_type, auto_approval_threshold_minor_units)
            VALUES (
                @process_id, @product_ref, @amount_minor_units, @source_account_ref,
                @interest_account_ref, @deposit_ref, @client_type, @auto_approval_threshold_minor_units)
            ON CONFLICT (process_id) DO NOTHING;
            """;

        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("process_id", reference.ProcessId);
        command.Parameters.AddWithValue("product_ref", reference.ProductRef);
        command.Parameters.AddWithValue("amount_minor_units", reference.AmountMinorUnits);
        command.Parameters.AddWithValue("source_account_ref", reference.SourceAccountRef);
        command.Parameters.AddWithValue("interest_account_ref", (object?)reference.InterestAccountRef ?? DBNull.Value);
        command.Parameters.AddWithValue("deposit_ref", reference.DepositRef);
        command.Parameters.AddWithValue("client_type", ClientTypeNames.ToName(reference.ClientType));
        command.Parameters.AddWithValue("auto_approval_threshold_minor_units", reference.AutoApprovalThresholdMinorUnits);

        return await command.ExecuteNonQueryAsync(ct) == 1;
    }

    /// <summary>
    /// Load the saga's pinned business references, or null if none were written (a saga started by
    /// the consume loop rather than the edge has no business-ref row — the fork and command assembly
    /// then fall back to the seam-level behaviour). Runs on the caller's transaction so it sees the
    /// references the same transaction may have just written.
    /// </summary>
    public async Task<SagaBusinessReference?> LoadAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid processId,
        CancellationToken ct = default)
    {
        // The 0006 shape ONLY (Fork B rework): no structural product facts — the engine resolves them.
        const string sql = """
            SELECT product_ref, amount_minor_units, source_account_ref, interest_account_ref,
                   deposit_ref, client_type, auto_approval_threshold_minor_units
            FROM saga_business_ref
            WHERE process_id = @process_id;
            """;

        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("process_id", processId);

        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            return null;
        }

        return new SagaBusinessReference(
            ProcessId: processId,
            ProductRef: reader.GetString(0),
            AmountMinorUnits: reader.GetInt64(1),
            SourceAccountRef: reader.GetString(2),
            InterestAccountRef: reader.IsDBNull(3) ? null : reader.GetString(3),
            DepositRef: reader.GetString(4),
            ClientType: ClientTypeNames.FromName(reader.GetString(5)),
            AutoApprovalThresholdMinorUnits: reader.GetInt64(6));
    }
}

/// <summary>
/// The verbatim SCREAMING_SNAKE codes the closed <see cref="ClientType"/> persists as in
/// <c>saga_business_ref.client_type</c> (the schema's CHECK constraint enforces the same vocabulary).
/// The persisted form is decoupled from the enum's declaration order so a reorder never silently
/// rewrites the meaning of a stored row.
/// </summary>
public static class ClientTypeNames
{
    /// <summary>The canonical persisted code for a <see cref="ClientType"/>.</summary>
    public static string ToName(ClientType clientType) => clientType switch
    {
        ClientType.Existing => "EXISTING",
        ClientType.New => "NEW",
        _ => throw new ArgumentOutOfRangeException(nameof(clientType), clientType, "Unknown client type."),
    };

    /// <summary>Parse a persisted code back to its <see cref="ClientType"/>. Throws on an unknown
    /// code — a row whose client_type does not round-trip is a corruption, not a tolerated value.</summary>
    public static ClientType FromName(string name) => name switch
    {
        "EXISTING" => ClientType.Existing,
        "NEW" => ClientType.New,
        _ => throw new ArgumentException($"Unknown persisted client type '{name}'.", nameof(name)),
    };
}
