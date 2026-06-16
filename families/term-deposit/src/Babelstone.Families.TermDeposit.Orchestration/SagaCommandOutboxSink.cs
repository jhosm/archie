using Babelstone.Orchestrator.Inbox;
using Npgsql;

namespace Babelstone.Families.TermDeposit.Orchestration;

/// <summary>
/// The REAL <see cref="ISagaCommandSink"/> (H.2, babelstone-n55u): it writes each command the
/// saga decided as a row in <c>saga_outbox</c> ON THE SAGA TRANSACTION, so the command commits
/// ATOMICALLY with the state move, the transition-history row, and the inbox dedup row
/// (ADR-IC-003 §P1 "saga-emitted commands use the same outbox mechanism as all other services
/// … not a separate publish path"). This replaces the substrate's
/// <see cref="RecordingCommandSink"/> (babelstone-mj2i) — the seam is unchanged; the writer
/// behind it is now durable. The drain (a relay, Epic E's mechanism) is the only reader and is
/// out of this issue's scope.
/// </summary>
/// <remarks>
/// <para>
/// <b>The impure shell owns the clock and the GUID (ADR-PC-010 §P5).</b> The ONE freshly minted
/// value is the delivery <c>message_id</c> (<see cref="Guid.NewGuid"/>) — an OPERATIONAL
/// identity, the dedup key a downstream consumer's inbox keys on, written to the outbox COLUMN.
/// The <c>created_at</c> wall-clock stamp is the DB column default (<c>clock_timestamp()</c>).
/// NEITHER rides the logical payload BODY: the body is built byte-stably from the seam's
/// references alone (process id, command type, identity trio), so re-emitting the same logical
/// command yields identical payload bytes (the byte-stability assertion). The minting and the
/// stamping happen HERE, in the shell, never inside any decider or fold.
/// </para>
/// <para>
/// <b>No PII on the row (ADR-PC-004 §P2 / no-PII-on-the-durable-bus).</b> Every column written —
/// process id, command type, causation/correlation references, the outbound <c>traceparent</c>,
/// and the structural payload — is a reference, never a NIF/IBAN/name/amount. The no-PII test
/// asserts this with a positive ALLOW-LIST over the written bytes, not a deny-list of forbidden
/// patterns.
/// </para>
/// <para>
/// <b>Distributed-trace propagation (H.5, ADR-IC-007 Layer 1).</b> The outbound W3C
/// <c>traceparent</c> the advance handler injects (its span's context) is written to the
/// OPERATIONAL <c>traceparent</c> COLUMN — like <c>message_id</c> and <c>created_at</c>, never the
/// byte-stable logical body. The drain re-emits it as the outbound Kafka header so the downstream
/// consumer threads its spans under this saga's trace. NULL when no tracer was listening.
/// </para>
/// <para>
/// <b>Full business-reference payloads (bd babelstone-t7o3.1).</b> The sink builds the FULL typed
/// <see cref="CommandPayload"/> through <see cref="SagaCommandPayloadFactory"/> from the saga's
/// pinned <see cref="SagaBusinessReference"/>s — the ReserveAccountBalance body carries the real
/// source account + a derived reservation reference, the ActivateDeposit body the deposit/Core-txn
/// references, and so on. The full payload stays byte-stable (every derived reference is a
/// deterministic function of the process id, never a minted value) and PII-free (a positive
/// allow-list of structural references).
/// </para>
/// <para>
/// <b>References are mandatory — fail-closed (bd babelstone-t7o3.9).</b> Every saga is started at the
/// edge (<c>EdgeSagaStarter</c>), which pins the business references in the SAME transaction as the
/// STARTED row, so they are ALWAYS present by the time any command is emitted. A saga that reaches
/// the sink with no pinned references throws rather than degrading to a minimal seam envelope — the
/// pre-production reference-less consume-loop fallback was removed (babelstone is not in production,
/// so no legacy start path needs preserving).
/// </para>
/// </remarks>
public sealed class SagaCommandOutboxSink(SagaBusinessReferenceStore? businessReferenceStore = null) : ISagaTypedCommandSink
{
    private readonly SagaBusinessReferenceStore _businessReferenceStore =
        businessReferenceStore ?? new SagaBusinessReferenceStore();

    /// <summary>The saga type this sink assembles command bodies for (bd babelstone-mtto PR2) — so the
    /// multi-saga <c>CompositeSagaCommandSink</c> routes the constitution saga's commands here.</summary>
    public string SagaType => ConstitutionProcess.Type;

    /// <inheritdoc />
    public async Task EmitAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid processId,
        string commandType,
        Guid causationMessageId,
        Guid? correlationId,
        CancellationToken ct = default,
        string? traceParent = null)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentException.ThrowIfNullOrWhiteSpace(commandType);

        // The LOGICAL payload body: the FULL typed command payload (the real account/deposit/Core
        // references) built through the pure factory from the saga's pinned business references. Every
        // saga is started at the edge, which pins those references in the SAME transaction as the
        // STARTED row (EdgeSagaStarter), so they are ALWAYS present here — a saga with none is a
        // fail-closed error, not a degraded seam-envelope path (bd babelstone-t7o3.9: babelstone is
        // pre-production; the consume-loop reference-less fallback was removed). The body is byte-stable
        // (NO Guid.NewGuid, NO DateTimeOffset.UtcNow inside): re-emitting the same logical command
        // produces identical bytes. The reference LOAD runs on the saga transaction, so it sees the row
        // the same transaction wrote at start.
        var reference = await _businessReferenceStore.LoadAsync(connection, transaction, processId, ct)
            ?? throw new InvalidOperationException(
                $"Saga {processId} has no pinned business references; every saga must be started at the edge " +
                $"(bd babelstone-t7o3.9). Cannot assemble the '{commandType}' command payload.");
        CommandPayload body =
            SagaCommandPayloadFactory.Build(commandType, processId, causationMessageId, correlationId, reference)
            ?? throw new InvalidOperationException(
                $"No command-payload recipe for '{commandType}' on saga {processId}; the factory must cover " +
                $"every command the state machine emits (bd babelstone-t7o3.9).");
        var payload = body.ToBytes();

        // The OPERATIONAL delivery id — the one freshly minted value, an outbox COLUMN, never in
        // the body. This is the impure shell; minting a GUID here is legitimate (it is not a
        // decider/fold). created_at is the DB column default (clock_timestamp()) — the wall clock
        // lives in the operational column, not the decision and not the body.
        var messageId = Guid.NewGuid();

        // The OUTBOUND W3C traceparent (H.5) is an OPERATIONAL column, like message_id and
        // created_at — NEVER in the logical payload body (which stays byte-stable: re-emitting the
        // same logical command yields identical bytes, and a traceparent changes per emission). The
        // drain re-emits it as the outbound Kafka header so the downstream consumer threads its
        // spans under this saga's trace (ADR-IC-007 Layer 1). NULL when no tracer was listening.
        const string sql = """
            INSERT INTO saga_outbox (message_id, process_id, command_type, causation_id, correlation_id, payload, traceparent)
            VALUES (@message_id, @process_id, @command_type, @causation_id, @correlation_id, @payload, @traceparent);
            """;

        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("message_id", messageId);
        command.Parameters.AddWithValue("process_id", processId);
        command.Parameters.AddWithValue("command_type", commandType);
        command.Parameters.AddWithValue("causation_id", causationMessageId);
        command.Parameters.AddWithValue("correlation_id", (object?)correlationId ?? DBNull.Value);
        command.Parameters.AddWithValue("payload", payload);
        command.Parameters.AddWithValue("traceparent", (object?)traceParent ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(ct);
    }
}
