using Npgsql;

namespace Babelstone.Lifecycle;

/// <summary>
/// PostgreSQL-backed <see cref="ISettlementHealthProbe"/> (ADR-PC-036 §Decision 4, LCD-2): reads the
/// orchestrator's <c>saga_state</c> row for the substrate-owned <c>SettlementProcess</c> and answers
/// "is this instance's cash leg parked in <c>HUMAN_INTERVENTION_REQUIRED</c>?". In plain terms: when the
/// settlement of a collected installment cannot be effected, the settlement saga parks in a state an
/// operator must resolve; this probe is how the lifecycle driver SEES that park, so the recurring path can
/// refuse to advance the schedule past money actually collected.
/// </summary>
/// <remarks>
/// <para>
/// <b>Read-only, and keyed exactly the way the saga is keyed.</b> The settlement saga instance for a
/// single-direction Movement-bearing event is keyed by the event's <c>ce_subject</c> — the engine relay
/// stamps that as the AGGREGATE id (ADR-IC-018 §P5), i.e. the same instance/stream id the lifecycle
/// driver's decisions carry. One indexed <c>EXISTS</c> over <c>(saga_type, state)</c> + the process-id key;
/// the probe never writes and never joins the orchestrator's other tables.
/// </para>
/// <para>
/// <b>Pinned wire literals, not a compile-time reference (the extraction-ready posture).</b>
/// <see cref="SettlementSagaType"/> / <see cref="ParkedState"/> mirror the orchestrator's
/// <c>SettlementProcess.Type</c> / <c>SettlementProcess.States.HumanInterventionRequired</c> — named
/// locally, not referenced, so the driver core takes no dependency on the orchestrator substrate assembly
/// (the same lock-step-by-constant discipline the rules use for command kinds and the fan-out uses for the
/// <c>movementdirections</c> header). The LCD-2 integration test asserts the two sides agree byte-for-byte.
/// </para>
/// <para>
/// <b>Known residual — fanned-out secondary legs.</b> A MULTI-direction Movement-bearing event's secondary
/// settlement legs (index ≥ 1) park at a DERIVED per-Movement subject (<c>SettlementMovementFanout</c>),
/// which this probe does not scan; the primary leg (index 0) keeps the instance id and IS scanned. Every
/// recurring lifecycle event gated today (a loan installment) carries a single movement, so the primary
/// covers the whole gated surface; a future multi-movement recurring event must widen this scan.
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
        // EXISTS over the (saga_type, state) index + the process-id primary key: is THIS instance's
        // settlement saga currently parked awaiting an operator? The saga row is keyed by ce_subject =
        // the aggregate id (ADR-IC-018 §P5), which is exactly the decision's InstanceId.
        const string sql = """
            SELECT EXISTS (
                SELECT 1
                FROM saga_state
                WHERE process_id = @process_id
                  AND saga_type = @saga_type
                  AND state = @state);
            """;

        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(ct);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("process_id", instanceId);
        command.Parameters.AddWithValue("saga_type", SettlementSagaType);
        command.Parameters.AddWithValue("state", ParkedState);

        var result = await command.ExecuteScalarAsync(ct);
        return result is true;
    }
}
