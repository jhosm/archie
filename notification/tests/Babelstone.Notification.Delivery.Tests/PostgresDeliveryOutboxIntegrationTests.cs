using Babelstone.Notification.Delivery.Migrations;
using Babelstone.TestFixtures;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace Babelstone.Notification.Delivery.Tests;

/// <summary>
/// One PostgreSQL container with the notification delivery estate's own migration set applied (the
/// <c>notification_delivery</c> series, ADR-IC-011 §P3; bd babelstone-60n8.10) — the same fixture shape
/// as the lifecycle driver's <c>LifecyclePostgresFixture</c> and the orchestrator's. Tests use fresh
/// notification ids, so a shared database needs no per-test reset.
/// </summary>
public sealed class DeliveryPostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _pg = new PostgreSqlBuilder("postgres:18-alpine").Build();

    public string ConnectionString => _pg.GetConnectionString();

    public async Task InitializeAsync()
    {
        await _pg.GatedStartAsync();
        await new MigrationRunner(ConnectionString).ApplyAsync();
    }

    public async Task DisposeAsync() => await _pg.DisposeAsync();
}

[CollectionDefinition(nameof(DeliveryPostgresCollection))]
public sealed class DeliveryPostgresCollection : ICollectionFixture<DeliveryPostgresFixture>;

/// <summary>
/// The durable delivery store against a REAL PostgreSQL (bd babelstone-60n8.10 — the crash-surviving
/// replacement for the in-memory v1, ADR-IC-011 §P3): idempotent enqueue on the composite
/// <c>notification_id</c> (ADR-PC-025 slot 4) that survives a "restart" (a fresh store instance over
/// the same database), the due-claim ordering, the §D4 status lifecycle with fail-loud unknown-id
/// marks, and the load-bearing transactional pair — the DEAD_LETTERED flip and the
/// <c>NotificationDeliveryExhausted</c> outbox row commit together, and the exhausted row drains
/// through claim → publish → PUBLISHED exactly once.
/// </summary>
[Trait("Category", "Integration")]
[Collection(nameof(DeliveryPostgresCollection))]
public sealed class PostgresDeliveryOutboxIntegrationTests(DeliveryPostgresFixture fixture)
{
    private static readonly DateTimeOffset T0 = new(2026, 7, 3, 8, 0, 0, TimeSpan.Zero);

    private PostgresDeliveryOutbox Store() => new(fixture.ConnectionString);

    private static NotificationDueSignal Signal(
        NotificationTriggerKind triggerKind = NotificationTriggerKind.Scheduled,
        Guid? customerRef = null) => new(
        NotificationId: Guid.NewGuid(),
        InstanceId: Guid.NewGuid(),
        CustomerRef: customerRef,
        TemplateRef: "pt.test.notice",
        TemplatePackVersion: "pt.2026.1",
        TriggerKind: triggerKind,
        CausationId: triggerKind == NotificationTriggerKind.EventDriven ? Guid.NewGuid() : null,
        Data: new Dictionary<string, string> { ["principal_cents"] = "1000000", ["maturity_date"] = "2026-09-01" },
        DueAt: new DateOnly(2026, 7, 3));

    [Fact]
    public async Task Migrations_are_idempotent_and_a_second_apply_is_a_no_op()
    {
        var applied = await new MigrationRunner(fixture.ConnectionString).ApplyAsync();

        Assert.Empty(applied); // the fixture already migrated — nothing pending
    }

    [Fact]
    public async Task Enqueue_is_idempotent_across_store_instances_and_terminal_states()
    {
        var signal = Signal();

        Assert.True(await Store().EnqueueAsync(signal, T0));
        // The idempotent re-present (ADR-PC-025 slot 4) — from a DIFFERENT store instance, the
        // crash/restart the in-memory v1 could not survive.
        Assert.False(await Store().EnqueueAsync(signal, T0.AddMinutes(5)));

        // Terminal rows keep absorbing: deliver it, then re-present again.
        await Store().MarkDeliveredAsync(signal.NotificationId, attempts: 1);
        Assert.False(await Store().EnqueueAsync(signal, T0.AddHours(1)));

        var record = await Store().GetAsync(signal.NotificationId);
        Assert.NotNull(record);
        Assert.Equal(DeliveryStatus.Delivered, record.Status);
        Assert.Equal(T0, record.EnqueuedAt); // the original enqueue instant, not the re-present's
    }

    [Fact]
    public async Task The_persisted_signal_round_trips_field_for_field()
    {
        var customerRef = Guid.NewGuid();
        var signal = Signal(NotificationTriggerKind.EventDriven, customerRef);

        await Store().EnqueueAsync(signal, T0);
        var record = await Store().GetAsync(signal.NotificationId);

        Assert.NotNull(record);
        // Field-by-field (the signal record's Data member is a dictionary, which record equality
        // compares by reference — persisted state must be compared by VALUE):
        Assert.Equal(signal.NotificationId, record.Signal.NotificationId);
        Assert.Equal(signal.InstanceId, record.Signal.InstanceId);
        Assert.Equal("pt.test.notice", record.Signal.TemplateRef);
        Assert.Equal("pt.2026.1", record.Signal.TemplatePackVersion);
        Assert.Equal(NotificationTriggerKind.EventDriven, record.Signal.TriggerKind);
        Assert.Equal(customerRef, record.Signal.CustomerRef);
        Assert.Equal(signal.CausationId, record.Signal.CausationId);
        Assert.Equal("1000000", record.Signal.Data["principal_cents"]);
        Assert.Equal(new DateOnly(2026, 7, 3), record.Signal.DueAt);
        Assert.Equal(0, record.Attempts);
        Assert.Equal(T0, record.NextAttemptAt); // due immediately
    }

    [Fact]
    public async Task Claim_returns_only_due_pending_rows_soonest_first_and_bounded()
    {
        var store = Store();
        var early = Signal();
        var later = Signal();
        var future = Signal();
        await store.EnqueueAsync(early, T0);
        await store.EnqueueAsync(later, T0.AddMinutes(1));
        await store.EnqueueAsync(future, T0.AddHours(2)); // not yet due at claim time

        var due = await store.ClaimDueAsync(T0.AddMinutes(30), limit: 10);
        var dueIds = due.Select(r => r.NotificationId).ToArray();

        Assert.Contains(early.NotificationId, dueIds);
        Assert.Contains(later.NotificationId, dueIds);
        Assert.DoesNotContain(future.NotificationId, dueIds);
        Assert.True(
            Array.IndexOf(dueIds, early.NotificationId) < Array.IndexOf(dueIds, later.NotificationId),
            "soonest-due first");

        var bounded = await store.ClaimDueAsync(T0.AddMinutes(30), limit: 1);
        Assert.Single(bounded);
    }

    [Fact]
    public async Task Attempt_failure_reschedules_and_keeps_the_row_pending()
    {
        var store = Store();
        var signal = Signal();
        await store.EnqueueAsync(signal, T0);

        var nextAttemptAt = T0.AddSeconds(30);
        await store.MarkAttemptFailedAsync(signal.NotificationId, attempts: 1, nextAttemptAt, "receiver answered 503");

        var record = await store.GetAsync(signal.NotificationId);
        Assert.NotNull(record);
        Assert.Equal(DeliveryStatus.Pending, record.Status);
        Assert.Equal(1, record.Attempts);
        Assert.Equal(nextAttemptAt, record.NextAttemptAt);
        Assert.Equal("receiver answered 503", record.LastError);

        // Not claimable before its retry time, claimable after.
        Assert.DoesNotContain(
            signal.NotificationId,
            (await store.ClaimDueAsync(T0.AddSeconds(10), 100)).Select(r => r.NotificationId));
        Assert.Contains(
            signal.NotificationId,
            (await store.ClaimDueAsync(T0.AddSeconds(60), 100)).Select(r => r.NotificationId));
    }

    [Fact]
    public async Task Abandonment_is_terminal_and_writes_no_exhausted_row()
    {
        var store = Store();
        var signal = Signal();
        await store.EnqueueAsync(signal, T0);

        await store.MarkAbandonedAsync(signal.NotificationId, attempts: 1, "receiver answered 404");

        var record = await store.GetAsync(signal.NotificationId);
        Assert.NotNull(record);
        Assert.Equal(DeliveryStatus.Abandoned, record.Status);
        // §D4 separates the two terminal failures: only EXHAUSTION announces on the backbone; the
        // misconfigured-endpoint case is the human-review residual, not an exhausted event.
        Assert.Equal(0, await CountExhaustedRowsAsync(signal.NotificationId));
    }

    [Fact]
    public async Task Dead_letter_flips_the_record_and_records_the_exhausted_announcement_atomically()
    {
        var store = Store();
        var customerRef = Guid.NewGuid();
        var signal = Signal(NotificationTriggerKind.EventDriven, customerRef);
        await store.EnqueueAsync(signal, T0);

        await store.MarkDeadLetteredAsync(signal.NotificationId, attempts: 10, "receiver answered 503");

        var record = await store.GetAsync(signal.NotificationId);
        Assert.NotNull(record);
        Assert.Equal(DeliveryStatus.DeadLettered, record.Status);
        Assert.Equal(10, record.Attempts);

        // The same transaction recorded the §D4 announcement (ADR-IC-011 §P3 step 7 / ADR-IC-004) —
        // claim it back through the relay's port and check the structural copy.
        var pending = await store.ClaimPendingAsync(100);
        var exhausted = Assert.Single(pending, e => e.NotificationId == signal.NotificationId);
        Assert.Equal(signal.InstanceId, exhausted.InstanceId);
        Assert.Equal(customerRef, exhausted.CustomerRef);
        Assert.Equal("pt.test.notice", exhausted.TemplateRef);
        Assert.Equal("pt.2026.1", exhausted.TemplatePackVersion);
        Assert.Equal(NotificationTriggerKind.EventDriven, exhausted.TriggerKind);
        Assert.Equal(10, exhausted.Attempts);
        Assert.Equal("receiver answered 503", exhausted.LastError);
        Assert.NotEqual(Guid.Empty, exhausted.EventId);

        // Publish flip: PUBLISHED rows leave the pending claim; a second flip of the same row is the
        // fail-loud wiring-bug path.
        await store.MarkPublishedAsync(signal.NotificationId);
        Assert.DoesNotContain(
            signal.NotificationId,
            (await store.ClaimPendingAsync(100)).Select(e => e.NotificationId));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.MarkPublishedAsync(signal.NotificationId));
    }

    [Fact]
    public async Task Marks_for_an_unknown_id_fail_loud()
    {
        var store = Store();
        var unknown = Guid.NewGuid();

        await Assert.ThrowsAsync<InvalidOperationException>(() => store.MarkDeliveredAsync(unknown, 1));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.MarkDeadLetteredAsync(unknown, 10, "x"));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => store.MarkAttemptFailedAsync(unknown, 1, T0, "x"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.MarkAbandonedAsync(unknown, 1, "x"));
        Assert.Null(await store.GetAsync(unknown));
    }

    private async Task<long> CountExhaustedRowsAsync(Guid notificationId)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT count(*) FROM notification_delivery_exhausted WHERE notification_id = @id;", connection);
        command.Parameters.AddWithValue("id", notificationId);
        return (long)(await command.ExecuteScalarAsync())!;
    }
}
