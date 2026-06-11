using System.Diagnostics;
using Babelstone.Orchestrator.Inbox;
using Babelstone.Orchestrator.Outbox;
using Babelstone.Orchestrator.Saga;
using Babelstone.Telemetry;
using Npgsql;
using Xunit;

namespace Babelstone.Orchestrator.Tests;

/// <summary>
/// Integration tests for the saga's distributed-trace coupling (H.5, babelstone-xol8) against a
/// real PostgreSQL: a saga advance opens ONE manual span on the SHARED <c>Babelstone.Engine</c>
/// source, PARENTED to the inbound event's W3C <c>traceparent</c>, carries the structural
/// <c>babelstone.saga.*</c> identifiers with NO PII, and threads its OWN context outbound as the
/// <c>traceparent</c> persisted on each emitted <c>saga_outbox</c> row — so the saga's work is one
/// connected distributed trace (ADR-IC-007 Layer 1; ADR-IC-003 §P3 "<c>process_id</c> and
/// <c>correlation_id</c> as span attributes for every span emitted by the orchestrator").
/// </summary>
[Trait("Category", "Integration")]
[Collection(nameof(OrchestratorPostgresCollection))]
public sealed class SagaTraceCouplingIntegrationTests(OrchestratorPostgresFixture fixture)
{
    private const string InboundTraceId = "4bf92f3577b34da6a3ce929d0e0e4736";
    private const string InboundSpanId = "00f067aa0ba902b7";
    private const string InboundTraceParent = $"00-{InboundTraceId}-{InboundSpanId}-01";

    // The structural babelstone.saga.* attribute keys the advance span must carry — operational
    // tier, never PII (ADR-PC-004 §P2 / ADR-IC-007 P4). The assertion is over KEYS, not values.
    private static readonly string[] PiiKeyFragments =
        ["nif", "iban", "account", "email", "phone", "address", "tax_id"];

    private readonly ConstitutionProcess _machine = new();
    private readonly SagaStateStore _stateStore = new();
    private readonly SagaTransitionLog _transitionLog = new();

    [Fact]
    public async Task A_migrated_saga_outbox_has_the_traceparent_column()
    {
        await using var connection = await OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT 1 FROM information_schema.columns " +
            "WHERE table_name = 'saga_outbox' AND column_name = 'traceparent';", connection);
        Assert.False(await command.ExecuteScalarAsync() is null or DBNull);
    }

    [Fact]
    public async Task An_advance_emits_a_span_parented_to_the_inbound_trace_with_structural_no_pii_tags()
    {
        var captured = new List<Activity>();
        using var listener = CaptureListener(captured);
        ActivitySource.AddActivityListener(listener);

        var processId = Guid.NewGuid();
        var correlationId = Guid.NewGuid();
        var handler = NewHandler(new SagaCommandOutboxSink());

        var started = Event(processId, ConstitutionProcess.ConstitutionRequested, correlationId, InboundTraceParent);
        Assert.Equal(AdvanceOutcome.Started, await RunAsync(handler, started));

        var span = Assert.Single(captured, a => a.OperationName == BabelstoneAttributes.SpanSagaAdvance);

        // Parented onto the inbound traceparent: same trace, the inbound span as parent — the saga
        // joins the upstream distributed trace as a child.
        Assert.Equal(InboundTraceId, span.TraceId.ToString());
        Assert.Equal(InboundSpanId, span.ParentSpanId.ToString());
        Assert.Equal(ActivityKind.Consumer, span.Kind);

        // ADR-IC-003 §P3: process_id + correlation_id on the span; plus the saga type, event type,
        // causation id, the state move, and the outcome.
        Assert.Equal(processId.ToString(), span.GetTagItem(BabelstoneAttributes.SagaProcessId));
        Assert.Equal(correlationId.ToString(), span.GetTagItem(BabelstoneAttributes.SagaCorrelationId));
        Assert.Equal(_machine.SagaType, span.GetTagItem(BabelstoneAttributes.SagaType));
        Assert.Equal(ConstitutionProcess.ConstitutionRequested, span.GetTagItem(BabelstoneAttributes.SagaEventType));
        Assert.Equal(started.MessageId.ToString(), span.GetTagItem(BabelstoneAttributes.SagaCausationId));
        Assert.Equal("STARTED->PARALLEL_VALIDATION", span.GetTagItem(BabelstoneAttributes.SagaTransition));
        Assert.Equal(nameof(AdvanceOutcome.Started), span.GetTagItem(BabelstoneAttributes.SagaOutcome));

        // Every tag key is operational-tier (babelstone.* structural), none PII-ish (the same
        // structural fitness function the engine's TelemetrySpanTests asserts).
        foreach (var tag in span.TagObjects)
        {
            Assert.StartsWith("babelstone.", tag.Key);
            var lowered = tag.Key.ToLowerInvariant();
            Assert.DoesNotContain(PiiKeyFragments, fragment => lowered.Contains(fragment));
        }
    }

    [Fact]
    public async Task The_emitted_outbox_rows_carry_the_outbound_traceparent_under_the_advance_span()
    {
        var captured = new List<Activity>();
        using var listener = CaptureListener(captured);
        ActivitySource.AddActivityListener(listener);

        var processId = Guid.NewGuid();
        var handler = NewHandler(new SagaCommandOutboxSink());

        Assert.Equal(
            AdvanceOutcome.Started,
            await RunAsync(handler, Event(processId, ConstitutionProcess.ConstitutionRequested, Guid.NewGuid(), InboundTraceParent)));

        var span = Assert.Single(captured, a => a.OperationName == BabelstoneAttributes.SpanSagaAdvance);
        var traceParents = await OutboxTraceParentsAsync(processId);

        // Two commands emitted; each carries an outbound traceparent that threads under THIS span
        // (same trace id, the advance span's id as parent) — the downstream consumer continues the
        // saga's trace. The trace id stays the upstream one (the saga is one trace end-to-end).
        Assert.Equal(2, traceParents.Count);
        Assert.All(traceParents, tp =>
        {
            Assert.NotNull(tp);
            var ctx = SagaTraceContext.ParseTraceParent(tp);
            Assert.Equal(InboundTraceId, ctx.TraceId.ToString());
            Assert.Equal(span.SpanId.ToString(), ctx.SpanId.ToString());
        });
    }

    [Fact]
    public async Task An_advance_with_no_tracer_listening_writes_a_null_traceparent_and_still_advances()
    {
        // No ActivityListener attached ⇒ StartActivity returns null ⇒ the whole trace path is a
        // no-op: the advance still commits, and the outbox rows carry a NULL traceparent (a
        // downstream consumer roots its own trace). The trace coupling never gates the saga.
        var processId = Guid.NewGuid();
        var handler = NewHandler(new SagaCommandOutboxSink());

        Assert.Equal(
            AdvanceOutcome.Started,
            await RunAsync(handler, Event(processId, ConstitutionProcess.ConstitutionRequested, Guid.NewGuid())));

        var traceParents = await OutboxTraceParentsAsync(processId);
        Assert.Equal(2, traceParents.Count);
        Assert.All(traceParents, Assert.Null);
    }

    // --- helpers -----------------------------------------------------------------------

    private static ActivityListener CaptureListener(List<Activity> captured) => new()
    {
        ShouldListenTo = source => source.Name == BabelstoneTelemetry.ActivitySourceName,
        Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
        ActivityStopped = captured.Add,
    };

    private SagaAdvanceHandler NewHandler(ISagaCommandSink sink) =>
        new(_machine, _stateStore, _transitionLog, sink)
        {
            StartEventType = ConstitutionProcess.ConstitutionRequested,
        };

    private static SagaInboxEvent Event(
        Guid processId, string eventType, Guid? correlationId = null, string? traceParent = null) =>
        new(Guid.NewGuid(), processId, eventType, "deposits.process.events", correlationId, traceParent);

    private async Task<AdvanceOutcome> RunAsync(SagaAdvanceHandler handler, SagaInboxEvent message)
    {
        await using var connection = await OpenAsync();
        await using var tx = await connection.BeginTransactionAsync();
        var outcome = await handler.AdvanceAsync(connection, tx, message);
        await tx.CommitAsync();
        return outcome;
    }

    private async Task<IReadOnlyList<string?>> OutboxTraceParentsAsync(Guid processId)
    {
        await using var connection = await OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT traceparent FROM saga_outbox WHERE process_id = @p ORDER BY seq;", connection);
        command.Parameters.AddWithValue("p", processId);

        var rows = new List<string?>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add(reader.IsDBNull(0) ? null : reader.GetString(0));
        }

        return rows;
    }

    private async Task<NpgsqlConnection> OpenAsync()
    {
        var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        return connection;
    }
}
