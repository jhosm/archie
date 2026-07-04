using Npgsql;

namespace Babelstone.Lifecycle;

/// <summary>
/// PostgreSQL-backed <see cref="ISettlementHealthProbe"/> (ADR-PC-036 §Decision 4, LCD-2): reads the
/// orchestrator's <c>saga_state</c> rows for the substrate-owned <c>SettlementProcess</c> and answers
/// "is ANY of this instance's settlement occurrences parked in <c>HUMAN_INTERVENTION_REQUIRED</c>?". In
/// plain terms: when the settlement of a collected installment cannot be effected, that occurrence's
/// settlement saga parks in a state an operator must resolve; this probe is how the lifecycle driver SEES
/// that park, so the recurring path can refuse to advance the schedule past money actually collected.
/// </summary>
/// <remarks>
/// <para>
/// <b>Read-only, and keyed by the SUBJECT linkage, not the saga instance id (ADR-PC-036 §Decision 4,
/// Revised 2026-07-04).</b> Settlement identity is PER OCCURRENCE (ADR-PC-032 §A9/§A10 Revised
/// 2026-07-04): each Originated Movement — installment 1, installment 2, each leg of a multi-direction
/// event — gets its own saga at a DERIVED <c>process_id</c>, while the account/instrument linkage (the
/// event's <c>ce_subject</c> = the aggregate/stream id the lifecycle driver's decisions carry) is
/// persisted on the indexed <c>saga_state.subject_id</c> column (orchestrator migration 0009). So the
/// gate's question is "is ANY settlement occurrence for THIS instance parked?" — one indexed
/// <c>EXISTS</c> over <c>subject_id</c> + the <c>(saga_type, state)</c> btree. This also closes the old
/// residual: a fanned-out secondary leg (index ≥ 1) carries the same <c>subject_id</c>, so a parked
/// secondary now holds the schedule too. The probe never writes and never joins the orchestrator's
/// other tables.
/// </para>
/// <para>
/// <b>Pinned wire literals, not a compile-time reference (the extraction-ready posture).</b>
/// <see cref="SettlementSagaType"/> / <see cref="ParkedState"/> mirror the orchestrator's
/// <c>SettlementProcess.Type</c> / <c>SettlementProcess.States.HumanInterventionRequired</c> — named
/// locally, not referenced, so the driver core takes no dependency on the orchestrator substrate assembly
/// (the same lock-step-by-constant discipline the rules use for command kinds and the fan-out uses for the
/// <c>movementdirections</c> header). The LCD-2 integration test asserts the two sides agree byte-for-byte,
/// and its walk over the orchestrator's REAL migrated schema is the tripwire for the <c>subject_id</c>
/// column shape itself — an orchestrator migration reshaping it breaks that test, not production.
/// </para>
/// <para>
/// A connection/query failure PROPAGATES (fail-closed): the pass treats it as backpressure and the
/// occurrence stays un-fired until the probe can answer — never "cannot see settlement, assume healthy".
/// </para>
/// </remarks>
public sealed class PostgresSettlementHealthProbe(string connectionString) : ISettlementHealthProbe
{
    /// <summary>The persisted <c>saga_state.saga_type</c> discriminator of the substrate-owned settlement
    /// saga. MUST equal the orchestrator's <c>SettlementProcess.Type</c> — pinned as a literal so the driver
    /// core stays free of an orchestrator compile dependency; the integration test asserts lock-step.</summary>
    public const string SettlementSagaType = "SettlementProcess";

    /// <summary>The parked state the gate holds on (ADR-PC-036 §Decision 4): the settlement saga's
    /// operator-escalation state. MUST equal the orchestrator's
    /// <c>SettlementProcess.States.HumanInterventionRequired</c> — pinned as a literal; the integration test
    /// asserts lock-step.</summary>
    public const string ParkedState = "HUMAN_INTERVENTION_REQUIRED";

    private readonly string _connectionString =
        !string.IsNullOrWhiteSpace(connectionString)
            ? connectionString
            : throw new ArgumentException(
                "The settlement-health probe needs the orchestrator saga_state connection string.",
                nameof(connectionString));

    /// <inheritdoc />
    public async Task<bool> IsParkedAsync(Guid instanceId, CancellationToken ct = default)
    {
        // EXISTS over the subject_id index (migration 0009) + the (saga_type, state) btree: is ANY of
        // THIS instance's settlement occurrences currently parked awaiting an operator? Each occurrence
        // is its own saga at a derived process_id (ADR-PC-032 §A9/§A10 Revised 2026-07-04); the
        // subject_id column carries the ce_subject = aggregate id linkage, which is exactly the
        // decision's InstanceId (ADR-PC-036 §Decision 4 Revised 2026-07-04).
        const string sql = """
            SELECT EXISTS (
                SELECT 1
                FROM saga_state
                WHERE subject_id = @subject_id
                  AND saga_type = @saga_type
                  AND state = @state);
            """;

        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(ct);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("subject_id", instanceId);
        command.Parameters.AddWithValue("saga_type", SettlementSagaType);
        command.Parameters.AddWithValue("state", ParkedState);

        var result = await command.ExecuteScalarAsync(ct);
        return result is true;
    }
}
