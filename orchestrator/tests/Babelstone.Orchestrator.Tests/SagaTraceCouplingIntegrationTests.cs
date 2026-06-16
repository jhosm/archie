using System.Diagnostics;
using Babelstone.Orchestrator.Edge;
using Babelstone.Families.TermDeposit.Orchestration;
using Babelstone.Orchestrator.Inbox;
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

    private const long ThresholdCents = 1_000_00;

    private readonly ConstitutionProcess _machine = new();
    private readonly SagaStateStore _stateStore = new();
    private readonly SagaTransitionLog _transitionLog = new();
    private readonly SagaBusinessReferenceStore _businessRefStore = new();

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

        var correlationId = Guid.NewGuid();

        // The edge is the SOLE saga starter (bd babelstone-t7o3.9); it opens its OWN transaction, so
        // the inbound-traceparent assertions are driven on an ADVANCE consumed AFTER the start. Note
        // the edge-start emissions are captured too, but the advance span is the one under test.
        var processId = await StartSagaWithReferencesAsync(correlationId);
        captured.Clear();

        // A consumed advance (BalanceReserved) carrying the inbound W3C traceparent — the advance is
        // what the consume loop drives, and its span parents to the inbound trace.
        var advance = Event(processId, ConstitutionProcess.BalanceReserved, correlationId, InboundTraceParent);
        Assert.Equal(AdvanceOutcome.Advanced, await RunAsync(NewHandler(new SagaCommandOutboxSink(_businessRefStore)), advance));

        // Scope to THIS saga's advance span by its structural process_id tag, not the span name alone:
        // the ActivityListener is process-global, and parallel integration test classes now also emit
        // saga.advance spans (the dispatcher self-advances on a delivery outcome — bd babelstone-t7o3.8),
        // so a bare name match would capture a sibling saga's span and break Assert.Single.
        var span = Assert.Single(
            captured,
            a => a.OperationName == BabelstoneAttributes.SpanSagaAdvance
                && (string?)a.GetTagItem(BabelstoneAttributes.SagaProcessId) == processId.ToString());

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
        Assert.Equal(ConstitutionProcess.BalanceReserved, span.GetTagItem(BabelstoneAttributes.SagaEventType));
        Assert.Equal(advance.MessageId.ToString(), span.GetTagItem(BabelstoneAttributes.SagaCausationId));
        Assert.Equal("PARALLEL_VALIDATION->AWAIT_LIMITS_VALIDATED", span.GetTagItem(BabelstoneAttributes.SagaTransition));
        Assert.Equal(nameof(AdvanceOutcome.Advanced), span.GetTagItem(BabelstoneAttributes.SagaOutcome));

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

        var handler = NewHandler(new SagaCommandOutboxSink(_businessRefStore));

        // Edge-start the saga (its emissions carry no traceparent — the edge opens its own
        // transaction), then drive both validations to VALIDATIONS_COMPLETE under the inbound trace.
        // The completing advance self-emits ConfirmDebit — the command emitted UNDER the advance span,
        // so its outbound traceparent is what threads downstream under the saga's trace.
        var processId = await StartSagaWithReferencesAsync(Guid.NewGuid());
        captured.Clear();

        await RunAsync(handler, Event(processId, ConstitutionProcess.LimitsValidated, traceParent: InboundTraceParent));
        Assert.Equal(
            AdvanceOutcome.Advanced,
            await RunAsync(handler, Event(processId, ConstitutionProcess.BalanceReserved, traceParent: InboundTraceParent)));

        // The completing advance is the one that emits ConfirmDebit; take its span (the last advance
        // span captured for THIS saga). Filter on the process_id tag, not the span name alone: the
        // process-global listener also captures parallel test classes' saga.advance spans now that the
        // dispatcher self-advances (bd babelstone-t7o3.8), so a sibling saga's span could otherwise win.
        var span = captured.Last(
            a => a.OperationName == BabelstoneAttributes.SpanSagaAdvance
                && (string?)a.GetTagItem(BabelstoneAttributes.SagaProcessId) == processId.ToString());

        // The outbox rows emitted UNDER an advance span carry a non-null outbound traceparent (the
        // edge-start rows carry null); each threads under THIS span (same trace id, the advance span's
        // id as parent) — the downstream consumer continues the saga's trace. The trace id stays the
        // upstream one (the saga is one trace end-to-end).
        var traceParents = (await OutboxTraceParentsAsync(processId)).Where(tp => tp is not null).ToList();
        Assert.NotEmpty(traceParents);
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
        var handler = NewHandler(new SagaCommandOutboxSink(_businessRefStore));

        // Edge-start, then drive both validations to VALIDATIONS_COMPLETE (the completing advance
        // self-emits ConfirmDebit) — all with no tracer listening. The advance still commits and the
        // emitted rows carry a NULL traceparent.
        var processId = await StartSagaWithReferencesAsync(Guid.NewGuid());

        await RunAsync(handler, Event(processId, ConstitutionProcess.LimitsValidated));
        Assert.Equal(
            AdvanceOutcome.Advanced,
            await RunAsync(handler, Event(processId, ConstitutionProcess.BalanceReserved)));

        // Every outbox row — the two edge-start validation commands and the self-emitted ConfirmDebit
        // — carries a NULL traceparent (no tracer was listening on any leg).
        var traceParents = await OutboxTraceParentsAsync(processId);
        Assert.NotEmpty(traceParents);
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
        new(_machine, _stateStore, _transitionLog, sink);

    private static SagaInboxEvent Event(
        Guid processId, string eventType, Guid? correlationId = null, string? traceParent = null) =>
        new(Guid.NewGuid(), processId, eventType, "deposits.process.events", correlationId, traceParent);

    // Start the saga through the REAL edge starter (the sole start path, bd babelstone-t7o3.9): creates
    // the STARTED row, pins the business references, drives STARTED + ConstitutionRequested →
    // PARALLEL_VALIDATION. Returns the minted internal process id the advances then drive.
    private async Task<Guid> StartSagaWithReferencesAsync(Guid correlationId)
    {
        var sink = new SagaCommandOutboxSink(_businessRefStore);
        var starter = new EdgeSagaStarter(_machine, _stateStore, _transitionLog, sink, _businessRefStore)
        {
            StartEventType = ConstitutionProcess.ConstitutionRequested,
        };

        var result = await starter.StartAsync(
            fixture.ConnectionString,
            owningClientId: "CLI-2026-007842",
            new EdgeBusinessFacts(
                ProductRef: "TD-TRAD-12M",
                AmountMinorUnits: 100_00,
                SourceAccountRef: "acct-ref-001",
                InterestAccountRef: null,
                ClientType: ClientType.Existing,
                AutoApprovalThresholdMinorUnits: ThresholdCents),
            correlationId);

        Assert.Equal(ConstitutionProcess.States.ParallelValidation, result.State);
        return result.ProcessId;
    }

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
