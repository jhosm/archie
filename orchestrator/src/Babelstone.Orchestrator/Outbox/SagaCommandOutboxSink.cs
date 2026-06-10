using Babelstone.Orchestrator.Commands;
using Babelstone.Orchestrator.Inbox;
using Npgsql;

namespace Babelstone.Orchestrator.Outbox;

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
/// process id, command type, causation/correlation references, and the structural payload — is
/// a reference, never a NIF/IBAN/name/amount. The no-PII test asserts this with a positive
/// ALLOW-LIST over the written bytes, not a deny-list of forbidden patterns.
/// </para>
/// </remarks>
public sealed class SagaCommandOutboxSink : ISagaCommandSink
{
    /// <inheritdoc />
    public async Task EmitAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid processId,
        string commandType,
        Guid causationMessageId,
        Guid? correlationId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentException.ThrowIfNullOrWhiteSpace(commandType);

        // The LOGICAL payload body — built ONLY from the seam's references (process id, command
        // type, identity trio). Byte-stable: NO Guid.NewGuid, NO DateTimeOffset.UtcNow inside.
        // Re-emitting the same logical command produces identical bytes.
        var body = new SagaCommandEnvelopeBody(commandType)
        {
            ProcessId = processId,
            CausationMessageId = causationMessageId,
            CorrelationId = correlationId,
        };
        var payload = body.ToBytes();

        // The OPERATIONAL delivery id — the one freshly minted value, an outbox COLUMN, never in
        // the body. This is the impure shell; minting a GUID here is legitimate (it is not a
        // decider/fold). created_at is the DB column default (clock_timestamp()) — the wall clock
        // lives in the operational column, not the decision and not the body.
        var messageId = Guid.NewGuid();

        const string sql = """
            INSERT INTO saga_outbox (message_id, process_id, command_type, causation_id, correlation_id, payload)
            VALUES (@message_id, @process_id, @command_type, @causation_id, @correlation_id, @payload);
            """;

        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("message_id", messageId);
        command.Parameters.AddWithValue("process_id", processId);
        command.Parameters.AddWithValue("command_type", commandType);
        command.Parameters.AddWithValue("causation_id", causationMessageId);
        command.Parameters.AddWithValue("correlation_id", (object?)correlationId ?? DBNull.Value);
        command.Parameters.AddWithValue("payload", payload);
        await command.ExecuteNonQueryAsync(ct);
    }
}
