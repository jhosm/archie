using Babelstone.Orchestrator.Inbox;
using Npgsql;

namespace Babelstone.Families.TermDeposit.Orchestration;

/// <summary>
/// The <see cref="ISagaCommandSink"/> for the <see cref="RenewalProcess"/> saga (bd babelstone-mtto;
/// the renewal counterpart of <see cref="SagaCommandOutboxSink"/>). It writes each renewal command the
/// saga decided as a row in <c>saga_outbox</c> ON THE SAGA TRANSACTION, so the command commits ATOMICALLY
/// with the state move, the transition-history row, and the inbox dedup row (ADR-IC-003 §P1). The
/// dispatcher (<c>SagaCommandDispatchDrainer</c>) is the only reader; it POSTs the row to the engine's
/// renewal legs, substituting the row's process_id into the {process_id} path template
/// (<see cref="RenewalCommandRouter"/>).
/// </summary>
/// <remarks>
/// <para>
/// <b>The impure shell owns the GUID + clock (ADR-PC-010 §P5).</b> The ONE freshly minted value is the
/// delivery <c>message_id</c> — the Idempotency-Key the engine dedups on (ADR-PC-029 slot 4) — written to
/// the outbox COLUMN, NEVER the body. <c>created_at</c> is the DB column default. The body is built
/// byte-stably by <see cref="RenewalCommandPayloadFactory"/> from the process id alone (the new deposit id
/// is the deterministic derivation, NO Guid.NewGuid; no wall clock — renewed_at is host-stamped
/// engine-side), so re-emitting the same logical command yields identical bytes and a crash-recovery
/// reissue is replayable.
/// </para>
/// <para>
/// <b>No per-saga state, NO product/role/funding config (UNLIKE the constitution sink).</b> The renewal
/// saga is EVENT-AUTO-STARTED off <c>DepositMatured</c> (no edge to pin references), and the engine
/// resolves EVERY renewal fact — including the product code, pricing role and funding account — from the
/// Matured closing deposit it loads (ADR-PC-009; bd babelstone-mtto.5). So this sink carries no
/// product-family knowledge (ADR-IC-003 §A7): the command body is the minimal <c>{ new_deposit_id }</c>.
/// PII-free (ADR-PC-004 §P2): a single derived deposit id — never a NIF/IBAN/name.
/// </para>
/// </remarks>
public sealed class RenewalCommandOutboxSink : ISagaTypedCommandSink
{
    /// <inheritdoc />
    public string SagaType => RenewalProcess.Type;

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

        // The LOGICAL payload body: the engine's renewal wire body, byte-stable (no minted GUID, no wall
        // clock). The factory must cover every command the renewal state machine emits — a miss is a
        // fail-closed wiring error, not a silent empty body.
        var payload = RenewalCommandPayloadFactory.Build(commandType, processId)
            ?? throw new InvalidOperationException(
                $"No renewal command-payload recipe for '{commandType}' on saga {processId}; the factory " +
                "must cover every command the RenewalProcess state machine emits (bd babelstone-mtto).");

        // The OPERATIONAL delivery id — the one freshly minted value, an outbox COLUMN, never in the body
        // (this is the impure shell; minting a GUID here is legitimate). created_at is the DB default.
        var messageId = Guid.NewGuid();

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
