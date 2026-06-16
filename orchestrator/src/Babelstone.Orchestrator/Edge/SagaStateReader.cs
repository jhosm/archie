using Babelstone.Orchestrator.Saga;
using Npgsql;

namespace Babelstone.Orchestrator.Edge;

/// <summary>
/// The SSE endpoint's READ side (ADR-IC-006 §P4 / Document 05 §Step 0): resolves a saga by its
/// client-facing <c>PROC-…</c> reference and polls its STRUCTURAL state for the stream. Read-only —
/// it never advances the saga (the consume loop does, #167); it observes the <c>saga_state</c> row
/// the loop mutates.
/// </summary>
/// <remarks>
/// <b>Structural state only (ADR-PC-004 §P2).</b> Every value this reader surfaces — the public
/// process reference, the business <c>state</c> name, the version, the owning <c>client_id</c> for
/// the authz check — is a structural reference, never a NIF/IBAN/name/amount. The SSE stream carries
/// the saga's state PROGRESSION (e.g. <c>PARALLEL_VALIDATION</c> → <c>DEPOSIT_CONSTITUTION_FAILED</c>),
/// never PII.
/// </remarks>
public sealed class SagaStateReader(string connectionString)
{
    private readonly string _connectionString = connectionString
        ?? throw new ArgumentNullException(nameof(connectionString));

    /// <summary>
    /// Resolve the saga for a client-facing <paramref name="publicProcessId"/>, or null if none was
    /// minted for it (the SSE 404 path). A plain read — no row lock — because the SSE path observes
    /// state, it never advances.
    /// </summary>
    public async Task<SagaInstance?> ResolveAsync(string publicProcessId, CancellationToken ct = default)
    {
        var stateStore = new SagaStateStore();
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        var saga = await stateStore.LoadByPublicIdAsync(connection, transaction, publicProcessId, ct);
        await transaction.RollbackAsync(ct);
        return saga;
    }

    /// <summary>
    /// Read the saga's current (state, version) by internal process id, or null if the row vanished.
    /// The SSE loop polls this to detect a state move (the loop owns the writes; this only reads).
    /// </summary>
    public async Task<(string State, long Version)?> CurrentAsync(Guid processId, CancellationToken ct = default)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(ct);
        await using var command = new NpgsqlCommand(
            "SELECT state, version FROM saga_state WHERE process_id = @p;", connection);
        command.Parameters.AddWithValue("p", processId);

        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            return null;
        }

        // The persisted state IS the saga's wire string (ADR-IC-018 §D3) — read verbatim, no central
        // enum round-trip. The SSE loop asks the routed machine whether it is terminal.
        return (reader.GetString(0), reader.GetInt64(1));
    }
}
