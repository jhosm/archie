using System.Text.Json;
using Babelstone.Engine;
using Babelstone.Engine.Hosting;
using Babelstone.EventStore;
using Babelstone.EventStore.Migrations;
using Babelstone.TestFixtures;
using Testcontainers.PostgreSql;
using Xunit;

namespace Babelstone.Engine.Api.Tests;

/// <summary>
/// One PostgreSQL container with the engine migration set applied, shared across the bulk-runner
/// test class (the <c>PostgresEventStoreFixture</c> shape). Tests register their own jobs over
/// fresh stream ids, so a shared database needs no per-test reset.
/// </summary>
public sealed class BulkOpsPostgresFixture : IAsyncLifetime
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

/// <summary>
/// Integration tests for the generic bulk-operations runner — the BULK_OP_REGISTER_DRAIN_COMPLETE
/// gate (ADR-PC-035) over real PostgreSQL work-tables and a real event store. In plain English: a
/// registered job's frozen target set drains to completion in bounded batches; an idempotent
/// re-drive appends nothing new; a mid-run host restart resumes from PENDING with no double-apply
/// (the deterministic command id dedupes); one failing item is isolated as FAILED and selectively
/// retryable; cancel stops further claims EVEN MID-RUN (the claim requires a DRAINING job) and a
/// cancelled job cannot be re-armed; the appender refuses a catalogued or unbound event; and
/// progress is a single query over the frozen set.
/// </summary>
[Trait("Category", "Integration")]
public sealed class BulkOperationRunnerIntegrationTests(BulkOpsPostgresFixture fixture)
    : IClassFixture<BulkOpsPostgresFixture>
{
    private static readonly DateTimeOffset Origin = new(2026, 6, 26, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task A_frozen_set_drains_to_completion_with_per_item_isolation_and_accurate_counts()
    {
        var runtime = Runtime();
        var applied = new[] { await SeedInstanceAsync(runtime), await SeedInstanceAsync(runtime), await SeedInstanceAsync(runtime) };
        var skipped = await SeedInstanceAsync(runtime);
        var poisoned = await SeedInstanceAsync(runtime);
        runtime.Strategy.FailInstances.Add(poisoned);

        var jobId = Guid.NewGuid();
        await runtime.Service.RegisterAsync(Registration(jobId, [
            .. applied.Select(id => new BulkTargetRegistration(id)),
            new BulkTargetRegistration(skipped, PreconditionInputJson: """{"skip":true}"""),
            new BulkTargetRegistration(poisoned),
        ]));

        // Batch size 2 over 5 targets: the drain must loop bounded batches to exhaustion (ADR-PC-035).
        var processed = await runtime.Drainer.DrainOnceAsync();

        Assert.Equal(5, processed);
        var progress = await runtime.Service.GetProgressAsync(jobId);
        Assert.Equal(new BulkOperationProgress(Total: 5, Applied: 3, Skipped: 1, Failed: 1, Pending: 0), progress);

        // One bad item never aborts the rest (ADR-PC-035): every healthy instance got exactly one event.
        foreach (var instanceId in applied)
        {
            Assert.Equal(1, await HeadSequenceAsync(runtime, instanceId));
            var target = await runtime.Store.ReadTargetAsync(jobId, instanceId);
            Assert.Equal("APPLIED", target!.Status);
            Assert.Equal(1, target.CommitSequence);
        }

        Assert.Equal(0, await HeadSequenceAsync(runtime, skipped));  // precondition declined: nothing appended
        Assert.Equal(0, await HeadSequenceAsync(runtime, poisoned)); // failed BEFORE the append
        var failedTarget = await runtime.Store.ReadTargetAsync(jobId, poisoned);
        Assert.Equal("FAILED", failedTarget!.Status);
        Assert.Contains("poisoned", failedTarget.FailureReason);

        // The job completes even with a FAILED item — failures are per-item and retryable (ADR-PC-035).
        Assert.Equal("COMPLETED", await JobStatusAsync(runtime, jobId));
    }

    [Fact]
    public async Task An_idempotent_redrive_appends_nothing_new_and_selective_retry_rearms_only_the_failed_subset()
    {
        var runtime = Runtime();
        var healthy = await SeedInstanceAsync(runtime);
        var flaky = await SeedInstanceAsync(runtime);
        runtime.Strategy.FailInstances.Add(flaky);

        var jobId = Guid.NewGuid();
        await runtime.Service.RegisterAsync(Registration(jobId, [
            new BulkTargetRegistration(healthy), new BulkTargetRegistration(flaky),
        ]));
        await runtime.Drainer.DrainOnceAsync();
        Assert.Equal("COMPLETED", await JobStatusAsync(runtime, jobId));

        // A re-drive of the completed job finds no active work: nothing processed, nothing appended.
        Assert.Equal(0, await runtime.Drainer.DrainOnceAsync());
        Assert.Equal(1, await HeadSequenceAsync(runtime, healthy));

        // Selective retry (ADR-PC-035): only the FAILED subset re-arms; the transient fault is gone.
        runtime.Strategy.FailInstances.Clear();
        Assert.Equal(1, await runtime.Service.RetryFailedAsync(jobId));

        await runtime.Drainer.DrainOnceAsync();

        var progress = await runtime.Service.GetProgressAsync(jobId);
        Assert.Equal(new BulkOperationProgress(Total: 2, Applied: 2, Skipped: 0, Failed: 0, Pending: 0), progress);
        // The retried item applied ONCE; the already-applied item was untouched by the retry.
        Assert.Equal(1, await HeadSequenceAsync(runtime, flaky));
        Assert.Equal(1, await HeadSequenceAsync(runtime, healthy));
        Assert.Equal("COMPLETED", await JobStatusAsync(runtime, jobId));
    }

    [Fact]
    public async Task A_restart_resumes_from_pending_with_no_double_apply()
    {
        var runtime = Runtime();
        var instances = new List<Guid>();
        for (var i = 0; i < 5; i++)
        {
            instances.Add(await SeedInstanceAsync(runtime));
        }

        var jobId = Guid.NewGuid();
        await runtime.Service.RegisterAsync(Registration(jobId, [.. instances.Select(id => new BulkTargetRegistration(id))]));

        // First "host": drain exactly ONE bounded batch (batch size 2), then vanish mid-run.
        await runtime.Store.MarkDrainingAsync(jobId);
        var firstBatch = await runtime.Store.DrainBatchAsync(
            jobId, batchSize: 2,
            target => ProcessLikeTheDrainerAsync(runtime, jobId, target));
        Assert.Equal(2, firstBatch);
        Assert.Equal(3, (await runtime.Service.GetProgressAsync(jobId)).Pending);

        // Second "host" (a fresh drainer over the same substrate): the work-table IS the to-do
        // list (ADR-PC-035) — it resumes from PENDING and finishes the job.
        var restarted = Runtime(runtime);
        var processed = await restarted.Drainer.DrainOnceAsync();

        Assert.Equal(3, processed);
        var progress = await runtime.Service.GetProgressAsync(jobId);
        Assert.Equal(new BulkOperationProgress(Total: 5, Applied: 5, Skipped: 0, Failed: 0, Pending: 0), progress);
        // No double-apply anywhere: every instance carries exactly its seed + ONE bulk event.
        foreach (var instanceId in instances)
        {
            Assert.Equal(1, await HeadSequenceAsync(runtime, instanceId));
        }
    }

    [Fact]
    public async Task A_reclaimed_already_appended_item_dedupes_on_the_deterministic_command_id()
    {
        var runtime = Runtime();
        var instanceId = await SeedInstanceAsync(runtime);
        var jobId = Guid.NewGuid();

        // Simulate the crash window BulkOperationCommandId exists for: the append committed but
        // the status flip was lost (the claim transaction rolled back), so the row is re-claimed
        // PENDING after restart. The pre-append here uses the SAME deterministic
        // (job_id, instance_id) command id the drainer will derive.
        await runtime.Appender.AppendAsync(
            instanceId,
            new BulkNoted(instanceId, "applied"),
            BulkOperationCommandId.For(jobId, instanceId),
            actor: "ops.test",
            validTime: Origin);
        Assert.Equal(1, await HeadSequenceAsync(runtime, instanceId));

        await runtime.Service.RegisterAsync(Registration(jobId, [new BulkTargetRegistration(instanceId)]));
        await runtime.Drainer.DrainOnceAsync();

        // The step deduped (ENGINE_COMMAND_IDEMPOTENT): recorded APPLIED with the ORIGINAL
        // receipt, and the stream still carries exactly one bulk event.
        var target = await runtime.Store.ReadTargetAsync(jobId, instanceId);
        Assert.Equal("APPLIED", target!.Status);
        Assert.Equal(1, target.CommitSequence);
        Assert.Equal(1, await HeadSequenceAsync(runtime, instanceId));
    }

    [Fact]
    public async Task Cancel_stops_further_claims_and_leaves_the_frozen_set_decidable()
    {
        var runtime = Runtime();
        var instanceId = await SeedInstanceAsync(runtime);
        var jobId = Guid.NewGuid();
        await runtime.Service.RegisterAsync(Registration(jobId, [new BulkTargetRegistration(instanceId)]));

        Assert.True(await runtime.Service.CancelAsync(jobId));
        var processed = await runtime.Drainer.DrainOnceAsync();

        Assert.Equal(0, processed);
        Assert.Equal("CANCELLED", await JobStatusAsync(runtime, jobId));
        Assert.Equal(0, await HeadSequenceAsync(runtime, instanceId)); // nothing appended
        Assert.Equal(1, (await runtime.Service.GetProgressAsync(jobId)).Pending); // still decidable by query

        // A cancelled plan stays cancelled: retry re-arms nothing (re-arming here would accumulate
        // rows the DRAINING-gated claim can never pick up — permanently pending, silently).
        Assert.Equal(0, await runtime.Service.RetryFailedAsync(jobId));
    }

    [Fact]
    public async Task A_cancel_mid_drain_stops_the_remaining_pending_rows()
    {
        var runtime = Runtime();
        var instances = new List<Guid>();
        for (var i = 0; i < 4; i++)
        {
            instances.Add(await SeedInstanceAsync(runtime));
        }

        var jobId = Guid.NewGuid();
        await runtime.Service.RegisterAsync(Registration(jobId, [.. instances.Select(id => new BulkTargetRegistration(id))]));

        // Pass pickup happened (the job is DRAINING) and one batch already applied — the exact
        // window where a drainer-side "is it cancelled?" check would race. The claim's own
        // DRAINING requirement is the guard: after the cancel it yields ZERO rows.
        await runtime.Store.MarkDrainingAsync(jobId);
        Assert.Equal(2, await runtime.Store.DrainBatchAsync(
            jobId, batchSize: 2, target => ProcessLikeTheDrainerAsync(runtime, jobId, target)));

        Assert.True(await runtime.Service.CancelAsync(jobId));

        Assert.Equal(0, await runtime.Store.DrainBatchAsync(
            jobId, batchSize: 2, target => ProcessLikeTheDrainerAsync(runtime, jobId, target)));
        Assert.Equal(0, await runtime.Drainer.DrainOnceAsync());

        // The remaining PENDING rows were never applied: their streams carry only the seed event.
        var progress = await runtime.Service.GetProgressAsync(jobId);
        Assert.Equal(2, progress.Applied + progress.Skipped + progress.Failed);
        Assert.Equal(2, progress.Pending);
        var untouched = 0;
        foreach (var instanceId in instances)
        {
            if (await HeadSequenceAsync(runtime, instanceId) == 0)
            {
                untouched++;
            }
        }

        Assert.Equal(2, untouched);
    }

    [Fact]
    public async Task The_appender_refuses_a_catalogued_integration_event()
    {
        var runtime = Runtime();
        var instanceId = await SeedInstanceAsync(runtime);

        // The ADR-IC-017 store-only guard: this appender writes no outbox rows, so a catalogued
        // event would silently lose its bus leg — it must refuse loud instead.
        var cataloguingAppender = new BulkInstanceAppender(
            runtime.Events, runtime.Serializer, new EverythingCatalogued(), [runtime.Module], TimeProvider.System);

        var refusal = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            cataloguingAppender.AppendAsync(
                instanceId, new BulkNoted(instanceId, "applied"), Guid.NewGuid(), "ops.test", Origin));

        Assert.Contains("catalogued", refusal.Message);
        Assert.Equal(0, await HeadSequenceAsync(runtime, instanceId)); // nothing appended
    }

    [Fact]
    public async Task The_appender_refuses_an_event_the_instances_family_does_not_bind()
    {
        var runtime = Runtime();
        var instanceId = await SeedInstanceAsync(runtime);

        // The fail-closed fold stance: an event the instance's own family cannot fold/replay must
        // never be appended to its stream.
        var refusal = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            runtime.Appender.AppendAsync(
                instanceId, new UnboundEvent(instanceId), Guid.NewGuid(), "ops.test", Origin));

        Assert.Contains("no handler binding", refusal.Message);
        Assert.Equal(0, await HeadSequenceAsync(runtime, instanceId)); // nothing appended
    }

    [Fact]
    public async Task A_job_with_no_registered_adapter_fails_loud_by_query()
    {
        var runtime = Runtime();
        var instanceId = await SeedInstanceAsync(runtime);
        var jobId = Guid.NewGuid();
        await runtime.Service.RegisterAsync(
            Registration(jobId, [new BulkTargetRegistration(instanceId)]) with { OperationKind = "NoSuchOp" });

        await runtime.Drainer.DrainOnceAsync();

        Assert.Equal("FAILED", await JobStatusAsync(runtime, jobId));
        Assert.Equal(0, await HeadSequenceAsync(runtime, instanceId));
    }

    // --- the synthetic bulk-test family + strategy ---

    private sealed record TestState;

    private sealed record InstanceSeeded(Guid InstanceId) : DomainEvent;

    private sealed record BulkNoted(Guid InstanceId, string Note) : DomainEvent;

    private sealed class NoOp<TEvent> : IEventHandler<TestState, TEvent> where TEvent : DomainEvent
    {
        public HandlerResult<TestState> Apply(TestState state, TEvent @event) =>
            HandlerResult<TestState>.From(state);
    }

    private sealed class BulkTestFamilyModule(string familyName) : IFamilyModule
    {
        public string FamilyName => familyName;
        public string SchemaVersion => $"{familyName}@1";
        public IReadOnlyList<HandlerRegistration> Handlers =>
        [
            new($"{familyName}.Seeded", typeof(InstanceSeeded),
                new DispatchableHandler<TestState, InstanceSeeded>(new NoOp<InstanceSeeded>())),
            // The store-only per-instance fact the test operation appends — uncatalogued, so the
            // appender's ADR-IC-017 store-only guard admits it.
            new($"{familyName}.BulkNoted", typeof(BulkNoted),
                new DispatchableHandler<TestState, BulkNoted>(new NoOp<BulkNoted>())),
        ];
    }

    /// <summary>The ADR-PC-035 adapter shape under test: an optional precondition (skip when the
    /// frozen input says so) + a per-instance event factory (throwing for poisoned instances, to
    /// exercise per-item isolation).</summary>
    private sealed class TestStrategy : IBulkOperationStrategy
    {
        public HashSet<Guid> FailInstances { get; } = [];

        public string OperationKind => "TestOp";

        public BulkPreconditionVerdict EvaluatePrecondition(BulkOperationTargetRow target) =>
            target.PreconditionInputJson is not null
            && JsonDocument.Parse(target.PreconditionInputJson).RootElement.TryGetProperty("skip", out var skip)
            && skip.GetBoolean()
                ? new BulkPreconditionVerdict.Skip("declined by test precondition")
                : new BulkPreconditionVerdict.Apply();

        public DomainEvent CreateEvent(BulkOperationTargetRow target) =>
            FailInstances.Contains(target.InstanceId)
                ? throw new InvalidOperationException("poisoned test instance")
                : new BulkNoted(target.InstanceId, "applied");
    }

    private sealed class TestJsonEventSerializer : IEventSerializer
    {
        public EncodedPayload Encode(DomainEvent @event) =>
            new(JsonSerializer.SerializeToUtf8Bytes(@event, @event.GetType()), 0);

        public DomainEvent Decode(ReadOnlyMemory<byte> payload, Type payloadType) =>
            (DomainEvent)JsonSerializer.Deserialize(payload.Span, payloadType)!;
    }

    private sealed class NothingCatalogued : IIntegrationEventCatalog
    {
        public bool IsCataloguedIntegrationEvent(string eventType) => false;
    }

    private sealed class EverythingCatalogued : IIntegrationEventCatalog
    {
        public bool IsCataloguedIntegrationEvent(string eventType) => true;
    }

    // Deliberately registered in NO family module: the appender must refuse it fail-closed.
    private sealed record UnboundEvent(Guid InstanceId) : DomainEvent;

    private sealed record TestRuntime(
        BulkOperationService Service,
        BulkOperationDrainer Drainer,
        IBulkOperationStore Store,
        BulkInstanceAppender Appender,
        PostgresEventStore Events,
        IFamilyModule Module,
        IEventSerializer Serializer,
        TestStrategy Strategy);

    /// <summary>A fresh runtime; pass <paramref name="shareSubstrateWith"/> to model a RESTARTED
    /// host over the same family/streams (new drainer + appender instances, same database).</summary>
    private TestRuntime Runtime(TestRuntime? shareSubstrateWith = null)
    {
        var module = shareSubstrateWith?.Module
            ?? new BulkTestFamilyModule($"bulktest_{Guid.NewGuid():N}");
        var strategy = shareSubstrateWith?.Strategy ?? new TestStrategy();
        var events = new PostgresEventStore(fixture.ConnectionString);
        var store = new PostgresBulkOperationStore(fixture.ConnectionString);
        var serializer = new TestJsonEventSerializer();
        var appender = new BulkInstanceAppender(
            events, serializer, new NothingCatalogued(), [module], TimeProvider.System);
        var drainer = new BulkOperationDrainer(store, appender, [strategy]);
        return new TestRuntime(
            new BulkOperationService(store), drainer, store, appender, events, module, serializer, strategy);
    }

    private static BulkOperationRegistration Registration(
        Guid jobId, IReadOnlyList<BulkTargetRegistration> targets) => new(
            JobId: jobId,
            OperationKind: "TestOp",
            MatchedSetJson: """{"kind":"explicit_ids"}""",
            RequestedBatchSize: 2,
            Actor: "ops.test",
            Targets: targets);

    /// <summary>Seed one instance stream with a single real appended event; returns its id.</summary>
    private async Task<Guid> SeedInstanceAsync(TestRuntime runtime)
    {
        var instanceId = Guid.NewGuid();
        var encoded = runtime.Serializer.Encode(new InstanceSeeded(instanceId));
        await runtime.Events.AppendAsync(
            instanceId,
            expectedVersion: -1,
            events:
            [
                new EventEnvelope(
                    EventId: Guid.NewGuid(),
                    StreamId: instanceId,
                    SequenceNumber: 0,
                    EventType: $"{runtime.Module.FamilyName}.Seeded",
                    EventSchemaVersion: 1,
                    Family: runtime.Module.FamilyName,
                    PartitionKey: instanceId,
                    PackVersion: "test",
                    SchemaVersion: runtime.Module.SchemaVersion,
                    ValidTime: Origin,
                    TransactionTime: Origin,
                    CausationId: null,
                    CorrelationId: null,
                    Actor: "test",
                    Payload: encoded.Bytes,
                    PayloadSchemaId: encoded.SchemaId),
            ],
            outboxRows: []);
        return instanceId;
    }

    // Mirrors BulkOperationDrainer.ProcessTargetAsync for the single-batch "first host" leg of the
    // restart test (the drainer's own loop would drain to completion).
    private async Task<BulkTargetOutcome> ProcessLikeTheDrainerAsync(
        TestRuntime runtime, Guid jobId, BulkOperationTargetRow target)
    {
        var @event = runtime.Strategy.CreateEvent(target);
        var commit = await runtime.Appender.AppendAsync(
            target.InstanceId, @event, BulkOperationCommandId.For(jobId, target.InstanceId),
            actor: "ops.test", validTime: Origin);
        return BulkTargetOutcome.Applied(commit);
    }

    private async Task<long> HeadSequenceAsync(TestRuntime runtime, Guid streamId)
    {
        long head = -1;
        await foreach (var envelope in runtime.Events.LoadAsync(streamId))
        {
            head = envelope.SequenceNumber;
        }

        return head;
    }

    private async Task<string> JobStatusAsync(TestRuntime runtime, Guid jobId)
    {
        await using var connection = new Npgsql.NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new Npgsql.NpgsqlCommand(
            "SELECT status FROM bulk_operation_jobs WHERE job_id = @job_id;", connection);
        command.Parameters.AddWithValue("job_id", jobId);
        return (string)(await command.ExecuteScalarAsync())!;
    }
}

/// <summary>
/// Pins the BulkOperationCommandId derivation the whole no-double-append guarantee rests on —
/// the determinism test ADR-PC-035 requires the implementing change to carry.
/// </summary>
public sealed class BulkOperationCommandIdTests
{
    [Fact]
    public void The_same_job_and_instance_always_derive_the_same_command_id()
    {
        var jobId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var instanceId = Guid.Parse("22222222-2222-2222-2222-222222222222");

        Assert.Equal(
            BulkOperationCommandId.For(jobId, instanceId),
            BulkOperationCommandId.For(jobId, instanceId));
    }

    [Fact]
    public void Distinct_jobs_or_instances_derive_distinct_command_ids()
    {
        var jobA = Guid.NewGuid();
        var jobB = Guid.NewGuid();
        var instance = Guid.NewGuid();

        Assert.NotEqual(BulkOperationCommandId.For(jobA, instance), BulkOperationCommandId.For(jobB, instance));
        Assert.NotEqual(BulkOperationCommandId.For(jobA, instance), BulkOperationCommandId.For(jobA, Guid.NewGuid()));
    }

    [Fact]
    public void The_derived_id_is_a_v5_style_rfc4122_uuid()
    {
        // The derivation stamps version/variant on indices 6/8 of the .NET-order byte array it
        // constructs the Guid from, so ToByteArray() round-trips them at the same indices.
        var bytes = BulkOperationCommandId.For(Guid.NewGuid(), Guid.NewGuid()).ToByteArray();

        Assert.Equal(0x50, bytes[6] & 0xF0);        // version 5 nibble
        Assert.Equal(0x80, bytes[8] & 0xC0);        // RFC-4122 variant
    }
}
