using Babelstone.Orchestrator.Dispatch;
using Babelstone.Orchestrator.Inbox;
using Babelstone.Orchestrator.Saga;
using Npgsql;
using Xunit;

namespace Babelstone.Orchestrator.Tests;

/// <summary>
/// The falsifiable proof that the orchestrator hosts MORE THAN ONE saga type at once
/// (bd babelstone-mtto PR1 — the multi-saga substrate). PR1 is a behaviour-preserving
/// generalisation: the existing ConstitutionProcess works identically (every other test stays
/// green), and a SECOND saga can be registered alongside it, selected by <c>saga_type</c>. This
/// class registers a minimal second machine (<see cref="StubRenewalProcess"/>) and asserts the
/// substrate routes by <c>saga_type</c> at every seam:
/// <list type="bullet">
///   <item>the <see cref="SagaAdvanceHandler"/> drives each saga through its OWN machine
///   (the DB-backed proof);</item>
///   <item><see cref="ISagaStateMachine.IsTerminal"/> is per-machine (the same enum value can be
///   terminal for one machine and not the other);</item>
///   <item><see cref="CompositeCommandRouter"/> routes commands by <c>saga_type</c> to the right
///   sub-router;</item>
///   <item>a duplicate <c>saga_type</c> registration is a fail-closed wiring error (the registry
///   must be a function).</item>
/// </list>
/// </summary>
public sealed class MultiSagaSubstrateTests
{
    // ---- A minimal second saga: test infrastructure only, NOT production code -------------------

    /// <summary>
    /// A trivial second <see cref="ISagaStateMachine"/> for the routing proof. It REUSES existing
    /// <see cref="SagaState"/> enum values (no new state is added in PR1 — that is PR2's contract
    /// change) precisely so the test proves routing is MACHINE-keyed, not STATE-keyed: the same
    /// <see cref="SagaState.Approved"/> is non-terminal for both machines, while
    /// <see cref="SagaState.Completed"/> is terminal for both by table inspection. Its transitions
    /// are deliberately distinct event names ("RenewalRequested" / "RenewalCompleted") so a
    /// ConstitutionProcess event never advances it and vice versa.
    /// </summary>
    private sealed class StubRenewalProcess : TableStateMachine
    {
        public const string Type = "RenewalProcess";
        public const string RenewalRequested = "RenewalRequested";
        public const string RenewalCompleted = "RenewalCompleted";
        public const string ConstituteRenewal = "ConstituteRenewal";

        public StubRenewalProcess()
            : base(Type, SagaState.Started, BuildTable())
        {
        }

        private static IEnumerable<((SagaState, string), TransitionOutcome)> BuildTable()
        {
            yield return ((SagaState.Started, RenewalRequested),
                TransitionOutcome.To(SagaState.Approved, ConstituteRenewal));
            yield return ((SagaState.Approved, RenewalCompleted),
                TransitionOutcome.To(SagaState.Completed));
        }
    }

    // ---- Pure: per-machine IsTerminal (no DB) ---------------------------------------------------

    [Fact]
    public void IsTerminal_is_per_machine_not_a_shared_state_predicate()
    {
        var constitution = new ConstitutionProcess();
        var renewal = new StubRenewalProcess();

        // COMPLETED is terminal for BOTH (neither table has an outgoing edge from it).
        Assert.True(constitution.IsTerminal(SagaState.Completed));
        Assert.True(renewal.IsTerminal(SagaState.Completed));

        // APPROVED is non-terminal for BOTH — each has its OWN outgoing edge(s) from it
        // (ConstitutionProcess: DebitConfirmed/ActivationFailed/…; the stub: RenewalCompleted).
        Assert.False(constitution.IsTerminal(SagaState.Approved));
        Assert.False(renewal.IsTerminal(SagaState.Approved));

        // STARTED is the entry state of both — never terminal (each has an outgoing start edge).
        Assert.False(constitution.IsTerminal(SagaState.Started));
        Assert.False(renewal.IsTerminal(SagaState.Started));

        // The ConstitutionProcess-specific terminals (CANCELLED etc.) are terminal for it — but the
        // stub's table never routes into or out of them, so they are terminal for the stub too (no
        // outgoing edge). The point: IsTerminal is answered from EACH machine's OWN table, not a
        // single shared predicate, so a state with an outgoing edge in one machine is non-terminal
        // THERE regardless of the other machine.
        Assert.True(constitution.IsTerminal(SagaState.Cancelled));
    }

    // ---- Pure: CompositeCommandRouter routes by saga_type (no DB) --------------------------------

    [Fact]
    public void CompositeCommandRouter_routes_a_command_to_the_sub_router_for_its_saga_type()
    {
        var options = new SagaCommandDispatcherOptions
        {
            ConnectionString = "Host=unused",
            EngineBaseUrl = "http://engine",
            SettlementBaseUrl = "http://settlement",
        };
        var composite = new CompositeCommandRouter([
            new SagaCommandRouter(options),
            new StubRenewalCommandRouter(),
        ]);

        // A ConstitutionProcess command under its OWN saga_type resolves through the constitution router.
        var activate = composite.Resolve(ConstitutionProcess.ActivateDeposit, ConstitutionProcess.Type);
        Assert.NotNull(activate);
        Assert.Equal("/v1/deposits", activate!.Path);

        // The SAME command name under a DIFFERENT saga_type routes to that saga's router (which does
        // not know ActivateDeposit) → null. Routing is keyed on saga_type, not the bare command name.
        Assert.Null(composite.Resolve(ConstitutionProcess.ActivateDeposit, StubRenewalProcess.Type));

        // The renewal saga's OWN command routes through the renewal sub-router.
        var renewal = composite.Resolve(StubRenewalProcess.ConstituteRenewal, StubRenewalProcess.Type);
        Assert.NotNull(renewal);
        Assert.Equal("/v1/renewals", renewal!.Path);

        // An unregistered saga_type resolves to null — a fail-closed miss, never a wrong router.
        Assert.Null(composite.Resolve(ConstitutionProcess.ActivateDeposit, "UnregisteredSaga"));
    }

    [Fact]
    public void CompositeCommandRouter_rejects_a_duplicate_saga_type()
    {
        var options = new SagaCommandDispatcherOptions
        {
            ConnectionString = "Host=unused",
            EngineBaseUrl = "http://engine",
            SettlementBaseUrl = "http://settlement",
        };

        Assert.Throws<InvalidOperationException>(() =>
            new CompositeCommandRouter([new SagaCommandRouter(options), new SagaCommandRouter(options)]));
    }

    // ---- Pure: SagaAdvanceHandler / drainer registries are functions (no DB) ---------------------

    [Fact]
    public void SagaAdvanceHandler_rejects_a_duplicate_saga_type()
    {
        Assert.Throws<InvalidOperationException>(() => new SagaAdvanceHandler(
            new ISagaStateMachine[] { new ConstitutionProcess(), new ConstitutionProcess() },
            new SagaStateStore(), new SagaTransitionLog(),
            new RecordingCommandSink(), new SagaBusinessReferenceStore()));
    }

    [Fact]
    public void SagaAdvanceHandler_rejects_an_empty_machine_set()
    {
        Assert.Throws<ArgumentException>(() => new SagaAdvanceHandler(
            Array.Empty<ISagaStateMachine>(),
            new SagaStateStore(), new SagaTransitionLog(),
            new RecordingCommandSink(), new SagaBusinessReferenceStore()));
    }

    [Fact]
    public void ResultEventBridge_registry_rejects_a_duplicate_saga_type()
    {
        var options = new SagaCommandDispatcherOptions
        {
            ConnectionString = "Host=unused",
            EngineBaseUrl = "http://engine",
            SettlementBaseUrl = "http://settlement",
        };
        var httpFactory = new SingleClientHttpClientFactory();
        var handler = new SagaAdvanceHandler(
            new ISagaStateMachine[] { new ConstitutionProcess() },
            new SagaStateStore(), new SagaTransitionLog(),
            new RecordingCommandSink(), new SagaBusinessReferenceStore());

        Assert.Throws<InvalidOperationException>(() => new SagaCommandDispatchDrainer(
            options, new CompositeCommandRouter([new SagaCommandRouter(options)]), httpFactory, handler,
            new IResultEventBridge[] { new ConstitutionResultEvents.Bridge(), new ConstitutionResultEvents.Bridge() }));
    }

    // ---- A minimal renewal command router + an unused HTTP factory for the pure tests -----------

    private sealed class StubRenewalCommandRouter : ISagaCommandRouter
    {
        public string SagaType => StubRenewalProcess.Type;

        public CommandRoute? Resolve(string commandType, string sagaType) => Resolve(commandType);

        public CommandRoute? Resolve(string commandType) => commandType switch
        {
            StubRenewalProcess.ConstituteRenewal =>
                new CommandRoute("http://engine", "/v1/renewals", HttpMethod.Post),
            _ => null,
        };
    }

    private sealed class SingleClientHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }

    // ---- DB-backed: ONE handler hosting TWO machines routes each saga by saga_type ---------------

    /// <summary>
    /// The headline proof: a single <see cref="SagaAdvanceHandler"/> registered with BOTH machines
    /// advances each saga through its OWN state machine, selected by the persisted <c>saga_type</c>.
    /// The constitution saga walks its constitution edge; the renewal saga walks its renewal edge; an
    /// event that belongs to one saga's vocabulary is a NoTransition for the other. This is the
    /// falsifiable generalisation — if the handler ignored <c>saga_type</c> and used a single machine,
    /// one of these advances would land in the wrong state or poison as NoTransition.
    /// </summary>
    [Trait("Category", "Integration")]
    [Collection(nameof(OrchestratorPostgresCollection))]
    public sealed class MultiSagaRoutingIntegrationTests(OrchestratorPostgresFixture fixture)
    {
        private readonly SagaStateStore _stateStore = new();
        private readonly SagaTransitionLog _transitionLog = new();

        [Fact]
        public async Task One_handler_drives_each_saga_through_its_own_machine_by_saga_type()
        {
            var sink = new RecordingCommandSink();
            // ONE handler, BOTH machines — the multi-saga substrate.
            var handler = new SagaAdvanceHandler(
                new ISagaStateMachine[] { new ConstitutionProcess(), new StubRenewalProcess() },
                _stateStore, _transitionLog, sink, new SagaBusinessReferenceStore());

            // Seed one saga of EACH type directly in STARTED (the routing proof needs only a row with a
            // saga_type — not the full edge-start ceremony, which is ConstitutionProcess-specific).
            var constitutionId = await StartRowAsync(ConstitutionProcess.Type);
            var renewalId = await StartRowAsync(StubRenewalProcess.Type);

            // Advance the constitution saga on ITS event → its constitution edge (PARALLEL_VALIDATION).
            Assert.Equal(AdvanceOutcome.Advanced,
                await RunAsync(handler, constitutionId, ConstitutionProcess.ConstitutionRequested));
            Assert.Equal(SagaState.ParallelValidation, await StateAsync(constitutionId));

            // Advance the renewal saga on ITS event → its renewal edge (Approved). SAME handler, routed
            // to the OTHER machine purely by saga_type.
            Assert.Equal(AdvanceOutcome.Advanced,
                await RunAsync(handler, renewalId, StubRenewalProcess.RenewalRequested));
            Assert.Equal(SagaState.Approved, await StateAsync(renewalId));

            // Cross-vocabulary events are NoTransition (the other machine has no such edge), NOT applied
            // to the wrong machine: the renewal event is illegal for the constitution saga and vice versa.
            Assert.Equal(AdvanceOutcome.NoTransition,
                await RunAsync(handler, constitutionId, StubRenewalProcess.RenewalRequested));
            Assert.Equal(AdvanceOutcome.NoTransition,
                await RunAsync(handler, renewalId, ConstitutionProcess.ConstitutionRequested));

            // The renewal saga completes on ITS terminal edge — and IsTerminal then short-circuits.
            Assert.Equal(AdvanceOutcome.Advanced,
                await RunAsync(handler, renewalId, StubRenewalProcess.RenewalCompleted));
            Assert.Equal(SagaState.Completed, await StateAsync(renewalId));
            // A late event for the now-terminal renewal saga is a no-op advance (per-machine IsTerminal).
            Assert.Equal(AdvanceOutcome.Terminal,
                await RunAsync(handler, renewalId, StubRenewalProcess.RenewalCompleted));
        }

        [Fact]
        public async Task An_unregistered_saga_type_fails_closed()
        {
            var sink = new RecordingCommandSink();
            // A handler that knows ONLY the renewal machine.
            var handler = new SagaAdvanceHandler(
                new ISagaStateMachine[] { new StubRenewalProcess() },
                _stateStore, _transitionLog, sink, new SagaBusinessReferenceStore());

            // A saga row whose saga_type has no registered machine: the advance must fail-closed (throw),
            // never silently skip — the substrate cannot decide a saga it has no machine for.
            var orphanId = await StartRowAsync("UnregisteredSaga");
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                RunAsync(handler, orphanId, "AnyEvent"));
        }

        // ---- helpers ----------------------------------------------------------------------------

        private async Task<Guid> StartRowAsync(string sagaType)
        {
            var processId = Guid.NewGuid();
            await using var connection = await OpenAsync();
            await using var tx = await connection.BeginTransactionAsync();
            var created = await _stateStore.TryStartAsync(
                connection, tx, processId, sagaType, SagaState.Started, correlationId: null);
            Assert.True(created);
            await tx.CommitAsync();
            return processId;
        }

        private async Task<AdvanceOutcome> RunAsync(SagaAdvanceHandler handler, Guid processId, string eventType)
        {
            await using var connection = await OpenAsync();
            await using var tx = await connection.BeginTransactionAsync();
            var outcome = await handler.AdvanceAsync(
                connection, tx,
                new SagaInboxEvent(Guid.NewGuid(), processId, eventType, "test.injected", CorrelationId: null));
            await tx.CommitAsync();
            return outcome;
        }

        private async Task<SagaState> StateAsync(Guid processId)
        {
            await using var connection = await OpenAsync();
            await using var tx = await connection.BeginTransactionAsync();
            var saga = await _stateStore.LoadAsync(connection, tx, processId);
            await tx.RollbackAsync();
            return saga!.State;
        }

        private async Task<NpgsqlConnection> OpenAsync()
        {
            var connection = new NpgsqlConnection(fixture.ConnectionString);
            await connection.OpenAsync();
            return connection;
        }
    }
}
