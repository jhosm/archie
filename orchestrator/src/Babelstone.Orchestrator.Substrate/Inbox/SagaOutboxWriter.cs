using Npgsql;

namespace Babelstone.Orchestrator.Inbox;

/// <summary>
/// The substrate-owned writer for the <c>saga_outbox</c> store. ADR-IC-018 §D2 names the
/// <c>saga_state</c>/<c>saga_transition</c>/<c>saga_outbox</c> stores as SUBSTRATE components — the
/// dependency arrow is family → substrate, never the reverse — so the row write lives here, not in the
/// family sinks. It appends ONE command-outbox row ON THE CALLER'S SAGA TRANSACTION, so the row commits
/// ATOMICALLY with the state move, the transition-history row, and the inbox dedup row (ADR-IC-003 §P1
/// "saga-emitted commands use the same outbox mechanism as all other services … not a separate publish
/// path"). The dispatcher (<c>SagaCommandDispatchDrainer</c>) is the only reader.
/// </summary>
/// <remarks>
/// <para>
/// <b>Family-agnostic by construction (ADR-IC-018 §D2).</b> The writer owns the table name, the column
/// set, and the row shape — it takes only primitives plus the ALREADY-ASSEMBLED payload BYTES, naming no
/// family and referencing no family type. Each saga type's family module owns ONLY its command-payload
/// assembly (its <c>…PayloadFactory</c>); the row write — identical for every saga type — lives here once,
/// so a <c>saga_outbox</c> schema change touches this one file, not every family sink (it was previously
/// copied verbatim into each typed sink).
/// </para>
/// <para>
/// <b>The impure shell owns the operational identity (ADR-PC-010 §P5).</b> The ONE freshly minted value is
/// the delivery <c>message_id</c> (<see cref="Guid.NewGuid"/>) — the Idempotency-Key a downstream
/// consumer's inbox dedups on (ADR-PC-029 slot 4) — written to the outbox COLUMN and returned to the
/// caller, NEVER placed in the payload body. <c>created_at</c> is the DB column default
/// (<c>clock_timestamp()</c>). The outbound W3C <c>traceparent</c> (H.5, ADR-IC-007 Layer 1) is likewise
/// an OPERATIONAL column, never the logical body. The minting/stamping happen HERE, in the shell — never
/// inside a decider or fold — and none of the three ride the byte-stable payload (re-emitting the same
/// logical command yields identical payload bytes; a crash-recovery reissue is replayable).
/// </para>
/// <para>
/// <b>No PII on the row (ADR-PC-004 §P2 / no-PII-on-the-durable-bus).</b> Every column the writer sets —
/// process id, command type, causation/correlation references, the <c>traceparent</c>, and the structural
/// payload the family factory built — is a reference, never a NIF/IBAN/name/amount. The payload's
/// PII-freedom is the family factory's contract; the writer adds no business data of its own.
/// </para>
/// </remarks>
public sealed class SagaOutboxWriter
{
    private const string InsertSql = """
        INSERT INTO saga_outbox (message_id, process_id, command_type, causation_id, correlation_id, payload, traceparent)
        VALUES (@message_id, @process_id, @command_type, @causation_id, @correlation_id, @payload, @traceparent);
        """;

    /// <summary>
    /// Append one command-outbox row on the supplied saga transaction and return the freshly minted
    /// delivery <c>message_id</c>. <paramref name="payload"/> is the family's already-assembled,
    /// byte-stable, PII-free command body; the writer adds only the operational columns
    /// (<c>message_id</c>, the DB-default <c>created_at</c>, and the <paramref name="traceParent"/>).
    /// </summary>
    public async Task<Guid> AppendAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid processId,
        string commandType,
        Guid causationMessageId,
        Guid? correlationId,
        byte[] payload,
        string? traceParent = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentException.ThrowIfNullOrWhiteSpace(commandType);
        ArgumentNullException.ThrowIfNull(payload);

        // The OPERATIONAL delivery id — the one freshly minted value, an outbox COLUMN, never in the body
        // (this is the impure shell; minting a GUID here is legitimate). created_at is the DB default.
        var messageId = Guid.NewGuid();

        await using var command = new NpgsqlCommand(InsertSql, connection, transaction);
        command.Parameters.AddWithValue("message_id", messageId);
        command.Parameters.AddWithValue("process_id", processId);
        command.Parameters.AddWithValue("command_type", commandType);
        command.Parameters.AddWithValue("causation_id", causationMessageId);
        command.Parameters.AddWithValue("correlation_id", (object?)correlationId ?? DBNull.Value);
        command.Parameters.AddWithValue("payload", payload);
        command.Parameters.AddWithValue("traceparent", (object?)traceParent ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(ct);
        return messageId;
    }
}
