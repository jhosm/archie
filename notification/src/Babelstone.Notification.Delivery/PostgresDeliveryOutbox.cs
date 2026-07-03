using System.Text.Json;
using Npgsql;
using NpgsqlTypes;

namespace Babelstone.Notification.Delivery;

/// <summary>
/// The durable <see cref="IDeliveryOutbox"/> — the ADR-IC-011 §P3 PostgreSQL delivery store that
/// replaces <see cref="InMemoryDeliveryOutbox"/> behind the port (bd babelstone-60n8.10; the named
/// follow-up of PR #435). In plain terms: the transport's memory of what it owes now survives a crash —
/// an enqueued obligation is a committed row, a claim is a read of due PENDING rows, and every §D4
/// outcome is a committed flip. The store also implements <see cref="IExhaustedDeliveryOutbox"/>: the
/// §D4 dead-letter flip and the <c>NotificationDeliveryExhausted</c> outbox insert are ONE transaction
/// (ADR-IC-004), so "gave up" and "will announce it" can never diverge.
/// </summary>
/// <remarks>
/// <para>
/// <b>Raw Npgsql from a connection string</b> — the same shape as the engine's event store, the
/// orchestrator's substrate, and the lifecycle driver's ledger: each operation opens its own pooled
/// connection; no ORM, no shared data source. The runtime role (<c>babelstone_notification</c>) holds
/// exactly the enqueue/claim/flip envelope (ADR-PC-001 §P3, lifted).
/// </para>
/// <para>
/// <b>The port's semantics carry over unchanged.</b> Enqueue is idempotent on <c>notification_id</c>
/// (<c>INSERT … ON CONFLICT DO NOTHING</c> — terminal rows retained, so a late redelivery re-opens
/// nothing, ADR-PC-025 slot 4); claims take no lease (single drain worker, the documented
/// <see cref="IDeliveryOutbox"/> stance); a mark for an unknown id throws (a wiring bug, not a runtime
/// condition). With this store in place, <see cref="INotificationDueSource"/>'s
/// commit-offset-only-after-enqueue rule (ADR-IC-011 §P3 step 3) becomes ENFORCEABLE: the enqueue the
/// source awaits is a durable commit, not a dictionary insert — the §P3 contract-review residual this
/// store closes.
/// </para>
/// <para>
/// <b>NO PII at rest (ADR-PC-025 §PII).</b> Only the STRUCTURAL signal is persisted — ids, template
/// refs, the string→string data map (amounts as integer-cent strings, dates, rates), transport-status
/// text. Rendered content and render-time-resolved PII materialise per attempt and are never written.
/// </para>
/// </remarks>
public sealed class PostgresDeliveryOutbox(string connectionString) : IDeliveryOutbox, IExhaustedDeliveryOutbox
{
    private static readonly JsonSerializerOptions DataJson = new(); // plain string→string map, no policy

    private readonly string _connectionString =
        string.IsNullOrWhiteSpace(connectionString)
            ? throw new ArgumentException("A PostgreSQL connection string is required.", nameof(connectionString))
            : connectionString;

    /// <inheritdoc />
    public async Task<bool> EnqueueAsync(NotificationDueSignal signal, DateTimeOffset now, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(signal);

        const string sql = """
            INSERT INTO notification_delivery
                (notification_id, instance_id, customer_ref, template_ref, template_pack_version,
                 trigger_kind, causation_id, data, due_at, status, attempts, next_attempt_at, enqueued_at)
            VALUES
                (@notification_id, @instance_id, @customer_ref, @template_ref, @template_pack_version,
                 @trigger_kind, @causation_id, @data, @due_at, 'PENDING', 0, @now, @now)
            ON CONFLICT (notification_id) DO NOTHING;
            """;

        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(ct);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("notification_id", signal.NotificationId);
        command.Parameters.AddWithValue("instance_id", signal.InstanceId);
        command.Parameters.AddWithValue("customer_ref", (object?)signal.CustomerRef ?? DBNull.Value);
        command.Parameters.AddWithValue("template_ref", signal.TemplateRef);
        command.Parameters.AddWithValue("template_pack_version", signal.TemplatePackVersion);
        command.Parameters.AddWithValue("trigger_kind", TriggerKindWire.ToWire(signal.TriggerKind));
        command.Parameters.AddWithValue("causation_id", (object?)signal.CausationId ?? DBNull.Value);
        command.Parameters.Add(new NpgsqlParameter("data", NpgsqlDbType.Jsonb)
        {
            Value = JsonSerializer.Serialize(signal.Data, DataJson),
        });
        command.Parameters.AddWithValue("due_at", signal.DueAt);
        command.Parameters.AddWithValue("now", now.UtcDateTime);

        // ON CONFLICT DO NOTHING reports 0 rows on the idempotent re-present (ADR-PC-025 slot 4) —
        // pending or terminal, an existing row absorbs it.
        return await command.ExecuteNonQueryAsync(ct) > 0;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<DeliveryRecord>> ClaimDueAsync(
        DateTimeOffset now, int limit, CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);

        const string sql = """
            SELECT notification_id, instance_id, customer_ref, template_ref, template_pack_version,
                   trigger_kind, causation_id, data, due_at, status, attempts, next_attempt_at,
                   last_error, enqueued_at
            FROM notification_delivery
            WHERE status = 'PENDING' AND next_attempt_at <= @now
            ORDER BY next_attempt_at, notification_id
            LIMIT @limit;
            """;

        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(ct);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("now", now.UtcDateTime);
        command.Parameters.AddWithValue("limit", limit);

        var due = new List<DeliveryRecord>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            due.Add(ReadRecord(reader));
        }

        return due;
    }

    /// <inheritdoc />
    public Task MarkDeliveredAsync(Guid notificationId, int attempts, CancellationToken ct = default) =>
        TransitionAsync(
            notificationId,
            "status = 'DELIVERED', attempts = @attempts, last_error = NULL",
            command => command.Parameters.AddWithValue("attempts", attempts),
            ct);

    /// <inheritdoc />
    public Task MarkAttemptFailedAsync(
        Guid notificationId, int attempts, DateTimeOffset nextAttemptAt, string? reason, CancellationToken ct = default) =>
        TransitionAsync(
            notificationId,
            "attempts = @attempts, next_attempt_at = @next_attempt_at, last_error = @reason",
            command =>
            {
                command.Parameters.AddWithValue("attempts", attempts);
                command.Parameters.AddWithValue("next_attempt_at", nextAttemptAt.UtcDateTime);
                command.Parameters.AddWithValue("reason", (object?)reason ?? DBNull.Value);
            },
            ct);

    /// <inheritdoc />
    /// <remarks>
    /// The §D4 exhaustion is the transactional pair (ADR-IC-004 / ADR-IC-011 §P3 step 7): the
    /// DEAD_LETTERED flip and the <c>notification_delivery_exhausted</c> outbox insert commit together —
    /// a crash between them is impossible by construction, so every dead-letter WILL be announced on the
    /// backbone once the relay drains it.
    /// </remarks>
    public async Task MarkDeadLetteredAsync(
        Guid notificationId, int attempts, string? reason, CancellationToken ct = default)
    {
        const string flipSql = """
            UPDATE notification_delivery
            SET status = 'DEAD_LETTERED', attempts = @attempts, last_error = @reason
            WHERE notification_id = @notification_id;
            """;

        // The exhausted row copies the delivery row's structural identity in-database — one round
        // trip, no read-modify-write window. ON CONFLICT DO NOTHING makes a (bug-shaped) second
        // dead-letter of the same id harmless: one exhausted event per notification_id, ever.
        const string exhaustSql = """
            INSERT INTO notification_delivery_exhausted
                (notification_id, instance_id, customer_ref, template_ref, template_pack_version,
                 trigger_kind, attempts, last_error)
            SELECT notification_id, instance_id, customer_ref, template_ref, template_pack_version,
                   trigger_kind, @attempts, @reason
            FROM notification_delivery
            WHERE notification_id = @notification_id
            ON CONFLICT (notification_id) DO NOTHING;
            """;

        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);

        await using (var flip = new NpgsqlCommand(flipSql, connection, transaction))
        {
            flip.Parameters.AddWithValue("notification_id", notificationId);
            flip.Parameters.AddWithValue("attempts", attempts);
            flip.Parameters.AddWithValue("reason", (object?)reason ?? DBNull.Value);
            if (await flip.ExecuteNonQueryAsync(ct) == 0)
            {
                throw UnknownRecord(notificationId);
            }
        }

        await using (var exhaust = new NpgsqlCommand(exhaustSql, connection, transaction))
        {
            exhaust.Parameters.AddWithValue("notification_id", notificationId);
            exhaust.Parameters.AddWithValue("attempts", attempts);
            exhaust.Parameters.AddWithValue("reason", (object?)reason ?? DBNull.Value);
            await exhaust.ExecuteNonQueryAsync(ct);
        }

        await transaction.CommitAsync(ct);
    }

    /// <inheritdoc />
    public Task MarkAbandonedAsync(Guid notificationId, int attempts, string? reason, CancellationToken ct = default) =>
        TransitionAsync(
            notificationId,
            "status = 'ABANDONED', attempts = @attempts, last_error = @reason",
            command =>
            {
                command.Parameters.AddWithValue("attempts", attempts);
                command.Parameters.AddWithValue("reason", (object?)reason ?? DBNull.Value);
            },
            ct);

    /// <inheritdoc />
    public async Task<DeliveryRecord?> GetAsync(Guid notificationId, CancellationToken ct = default)
    {
        const string sql = """
            SELECT notification_id, instance_id, customer_ref, template_ref, template_pack_version,
                   trigger_kind, causation_id, data, due_at, status, attempts, next_attempt_at,
                   last_error, enqueued_at
            FROM notification_delivery
            WHERE notification_id = @notification_id;
            """;

        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(ct);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("notification_id", notificationId);

        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadRecord(reader) : null;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ExhaustedDelivery>> ClaimPendingAsync(int limit, CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);

        const string sql = """
            SELECT notification_id, event_id, instance_id, customer_ref, template_ref,
                   template_pack_version, trigger_kind, attempts, last_error, exhausted_at
            FROM notification_delivery_exhausted
            WHERE status = 'PENDING'
            ORDER BY exhausted_at, notification_id
            LIMIT @limit;
            """;

        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(ct);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("limit", limit);

        var pending = new List<ExhaustedDelivery>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            pending.Add(new ExhaustedDelivery(
                NotificationId: reader.GetGuid(0),
                EventId: reader.GetGuid(1),
                InstanceId: reader.GetGuid(2),
                CustomerRef: await reader.IsDBNullAsync(3, ct) ? null : reader.GetGuid(3),
                TemplateRef: reader.GetString(4),
                TemplatePackVersion: reader.GetString(5),
                TriggerKind: TriggerKindWire.FromWire(reader.GetString(6)),
                Attempts: reader.GetInt32(7),
                LastError: await reader.IsDBNullAsync(8, ct) ? null : reader.GetString(8),
                ExhaustedAt: new DateTimeOffset(reader.GetFieldValue<DateTime>(9), TimeSpan.Zero)));
        }

        return pending;
    }

    /// <inheritdoc />
    public async Task MarkPublishedAsync(Guid notificationId, CancellationToken ct = default)
    {
        const string sql = """
            UPDATE notification_delivery_exhausted
            SET status = 'PUBLISHED', published_at = clock_timestamp()
            WHERE notification_id = @notification_id AND status = 'PENDING';
            """;

        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(ct);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("notification_id", notificationId);
        if (await command.ExecuteNonQueryAsync(ct) == 0)
        {
            throw new InvalidOperationException(
                $"Exhausted-delivery outbox holds no PENDING row for notification '{notificationId}'.");
        }
    }

    private async Task TransitionAsync(
        Guid notificationId, string setClause, Action<NpgsqlCommand> bind, CancellationToken ct)
    {
        var sql = $"UPDATE notification_delivery SET {setClause} WHERE notification_id = @notification_id;";

        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(ct);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("notification_id", notificationId);
        bind(command);

        if (await command.ExecuteNonQueryAsync(ct) == 0)
        {
            // Fail loud at the seam (the same posture as InMemoryDeliveryOutbox): a mark for an unknown
            // id is a wiring bug (the pass only marks records it just claimed), never a runtime
            // condition to swallow.
            throw UnknownRecord(notificationId);
        }
    }

    private static InvalidOperationException UnknownRecord(Guid notificationId) =>
        new($"Delivery outbox holds no record for notification '{notificationId}'.");

    private static DeliveryRecord ReadRecord(NpgsqlDataReader reader)
    {
        var signal = new NotificationDueSignal(
            NotificationId: reader.GetGuid(0),
            InstanceId: reader.GetGuid(1),
            CustomerRef: reader.IsDBNull(2) ? null : reader.GetGuid(2),
            TemplateRef: reader.GetString(3),
            TemplatePackVersion: reader.GetString(4),
            TriggerKind: TriggerKindWire.FromWire(reader.GetString(5)),
            CausationId: reader.IsDBNull(6) ? null : reader.GetGuid(6),
            Data: JsonSerializer.Deserialize<Dictionary<string, string>>(reader.GetString(7), DataJson)
                ?? throw new InvalidOperationException("The persisted data map deserialised to null."),
            DueAt: reader.GetFieldValue<DateOnly>(8));

        return new DeliveryRecord(
            NotificationId: signal.NotificationId,
            Signal: signal,
            Status: StatusFromWire(reader.GetString(9)),
            Attempts: reader.GetInt32(10),
            EnqueuedAt: new DateTimeOffset(reader.GetFieldValue<DateTime>(13), TimeSpan.Zero),
            NextAttemptAt: new DateTimeOffset(reader.GetFieldValue<DateTime>(11), TimeSpan.Zero),
            LastError: reader.IsDBNull(12) ? null : reader.GetString(12));
    }

    private static DeliveryStatus StatusFromWire(string status) => status switch
    {
        "PENDING" => DeliveryStatus.Pending,
        "DELIVERED" => DeliveryStatus.Delivered,
        "DEAD_LETTERED" => DeliveryStatus.DeadLettered,
        "ABANDONED" => DeliveryStatus.Abandoned,
        _ => throw new InvalidOperationException($"Unknown persisted delivery status '{status}'."),
    };
}

/// <summary>
/// The <see cref="NotificationTriggerKind"/> ↔ governed wire symbol map (ADR-PC-025 §6 /
/// <c>contracts/avro/operations/NotificationDue.avsc</c>): the store persists the SCREAMING_SNAKE_CASE
/// contract symbols — the same rendering the webhook envelope and the Avro enum use — so a DBA reading
/// the table and a consumer reading the bus see one vocabulary.
/// </summary>
internal static class TriggerKindWire
{
    public static string ToWire(NotificationTriggerKind kind) => kind switch
    {
        NotificationTriggerKind.EventDriven => "EVENT_DRIVEN",
        NotificationTriggerKind.Scheduled => "SCHEDULED",
        NotificationTriggerKind.PreContractual => "PRE_CONTRACTUAL",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown trigger kind."),
    };

    public static NotificationTriggerKind FromWire(string symbol) => symbol switch
    {
        "EVENT_DRIVEN" => NotificationTriggerKind.EventDriven,
        "SCHEDULED" => NotificationTriggerKind.Scheduled,
        "PRE_CONTRACTUAL" => NotificationTriggerKind.PreContractual,
        _ => throw new InvalidOperationException($"Unknown persisted trigger kind '{symbol}'."),
    };
}
