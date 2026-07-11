using Babelstone.Orchestrator.Dispatch;
using Babelstone.Orchestrator.Inbox;
using Babelstone.Families.TermDeposit.Orchestration;
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
    /// <see cref="ConstitutionProcess.States.Approved"/> is non-terminal for both machines, while
    /// <see cref="ConstitutionProcess.States.Completed"/> is terminal for both by table inspection. Its transitions
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
            : base(Type, ConstitutionProcess.States.Started, BuildTable())
        {
        }

        private static IEnumerable<((string, string), TransitionOutcome)> BuildTable()
        {
            yield return ((ConstitutionProcess.States.Started, RenewalRequested),
                TransitionOutcome.To(ConstitutionProcess.States.Approved, ConstituteRenewal));
            yield return ((ConstitutionProcess.States.Approved, RenewalCompleted),
                TransitionOutcome.To(ConstitutionProcess.States.Completed));
        }
    }

    // ---- Pure: per-machine IsTerminal (no DB) ---------------------------------------------------

    [Fact]
    public void IsTerminal_is_per_machine_not_a_shared_state_predicate()
    {
        var constitution = new ConstitutionProcess();
        var renewal = new StubRenewalProcess();

        // COMPLETED is terminal for BOTH (neither table has an outgoing edge from it).
        Assert.True(constitution.IsTerminal(ConstitutionProcess.States.Completed));
        Assert.True(renewal.IsTerminal(ConstitutionProcess.States.Completed));

        // APPROVED is non-terminal for BOTH — each has its OWN outgoing edge(s) from it
        // (ConstitutionProcess: DebitConfirmed/ActivationFailed/…; the stub: RenewalCompleted).
        Assert.False(constitution.IsTerminal(ConstitutionProcess.States.Approved));
        Assert.False(renewal.IsTerminal(ConstitutionProcess.States.Approved));

        // STARTED is the entry state of both — never terminal (each has an outgoing start edge).
        Assert.False(constitution.IsTerminal(ConstitutionProcess.States.Started));
        Assert.False(renewal.IsTerminal(ConstitutionProcess.States.Started));

        // The ConstitutionProcess-specific terminals (CANCELLED etc.) are terminal for it — but the
        // stub's table never routes into or out of them, so they are terminal for the stub too (no
        // outgoing edge). The point: IsTerminal is answered from EACH machine's OWN table, not a
        // single shared predicate, so a state with an outgoing edge in one machine is non-terminal
        // THERE regardless of the other machine.
        Assert.True(constitution.IsTerminal(ConstitutionProcess.States.Cancelled));

        // HUMAN_INTERVENTION_REQUIRED is the behaviour-preserving override's locked invariant
        // (bd babelstone-mtto PR1). ConstitutionProcess.IsTerminal delegates to SagaStateNames.IsTerminal
        // (the pre-multi-saga predicate), so HIR stays NON-terminal — an operator resolves it (the
        // resolution edge arrives in PR2). The substrate DEFAULT (pure table inspection) would call HIR
        // terminal today (no outgoing edge), which is exactly the divergence the override exists to
        // prevent: this assertion fails the moment the override is dropped and the refactor's behaviour
        // change leaks back in. The stub, which never touches HIR, reports it terminal under the default —
        // the two machines giving DIFFERENT answers for the SAME state is the whole point of per-machine
        // IsTerminal.
        Assert.False(constitution.IsTerminal(ConstitutionProcess.States.HumanInterventionRequired));
        Assert.True(new StubRenewalProcess().IsTerminal(ConstitutionProcess.States.HumanInterventionRequired));
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
    public void CompositeCommandRouter_threads_the_settlement_target_header_to_the_constitution_router()
    {
        // The engine-CA funding wire (ADR-PC-043): a ce_settlementtarget=
        // engine-ca funding leg routes to the ENGINE-CA base URL through the counterparty-invariant path; a
        // legacy leg (absent/legacy-dda) stays on the legacy Core-ACL base URL. The composite forwards the
        // header to the sub-router — routing is header-only (the substrate never reads Movement.AccountRef).
        var options = new SagaCommandDispatcherOptions
        {
            ConnectionString = "Host=unused",
            EngineBaseUrl = "http://engine",
            SettlementBaseUrl = "http://legacy-acl",
            EngineCaSettlementBaseUrl = "http://engine-ca",
        };
        var composite = new CompositeCommandRouter([new SagaCommandRouter(options)]);

        var engineCaHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["settlementtarget"] = "engine-ca",
        };

        // A funding debit leg carrying engine-ca → the engine-CA counterparty, counterparty-invariant path.
        var reserveEngineCa = composite.Resolve(
            ConstitutionProcess.ReserveAccountBalance, ConstitutionProcess.Type, engineCaHeaders);
        Assert.NotNull(reserveEngineCa);
        Assert.Equal("http://engine-ca", reserveEngineCa!.BaseUrl);
        Assert.Equal("/v1/reservations", reserveEngineCa.Path);

        var confirmEngineCa = composite.Resolve(
            ConstitutionProcess.ConfirmDebit, ConstitutionProcess.Type, engineCaHeaders);
        Assert.NotNull(confirmEngineCa);
        Assert.Equal("http://engine-ca", confirmEngineCa!.BaseUrl);
        Assert.Equal("/v1/debits", confirmEngineCa.Path);

        // No header (legacy funding) → the legacy Core-ACL base URL, same route. Legacy routing UNCHANGED.
        var reserveLegacy = composite.Resolve(
            ConstitutionProcess.ReserveAccountBalance, ConstitutionProcess.Type, extensionHeaders: null);
        Assert.NotNull(reserveLegacy);
        Assert.Equal("http://legacy-acl", reserveLegacy!.BaseUrl);
        Assert.Equal("/v1/reservations", reserveLegacy.Path);

        // The engine-bound ActivateDeposit is counterparty-agnostic — always the engine, header or not.
        var activate = composite.Resolve(
            ConstitutionProcess.ActivateDeposit, ConstitutionProcess.Type, engineCaHeaders);
        Assert.NotNull(activate);
        Assert.Equal("http://engine", activate!.BaseUrl);
        Assert.Equal("/v1/deposits", activate.Path);
    }

    [Fact]
    public void CompositeCommandRouter_fails_closed_on_an_engine_ca_leg_with_no_engine_ca_base_url()
    {
        // An engine-ca leg with NO engine-CA base URL configured resolves to null — fail-closed (ADR-PC-043):
        // the drain surfaces a routing failure rather than silently settling engine-CA money on the legacy
        // core. Legacy legs are unaffected (they still route to SettlementBaseUrl).
        var options = new SagaCommandDispatcherOptions
        {
            ConnectionString = "Host=unused",
            EngineBaseUrl = "http://engine",
            SettlementBaseUrl = "http://legacy-acl",
            EngineCaSettlementBaseUrl = null,
        };
        var composite = new CompositeCommandRouter([new SagaCommandRouter(options)]);

        var engineCaHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["settlementtarget"] = "engine-ca",
        };

        Assert.Null(composite.Resolve(
            ConstitutionProcess.ReserveAccountBalance, ConstitutionProcess.Type, engineCaHeaders));

        // A legacy leg still routes (the null engine-CA base URL only fails an engine-ca-targeted leg).
        var legacy = composite.Resolve(
            ConstitutionProcess.ReserveAccountBalance, ConstitutionProcess.Type, extensionHeaders: null);
        Assert.NotNull(legacy);
        Assert.Equal("http://legacy-acl", legacy!.BaseUrl);
    }

    [Fact]
    public void ConstitutionPayloadFactory_tags_an_engine_owned_funding_account_as_engine_ca()
    {
        // The funding-account classification: an engine-owned current account's
        // account_ref IS the account GUID (AccountRef == AccountId.ToString()), so a GUID-shaped funding ref
        // → engine-ca (the debit funds itself from the customer CA, carrying the promoted destination
        // account_ref + amount + the hold-linking intent reference); a legacy opaque token → legacy-DDA (the
        // funding-leg extras serialize as explicit nulls, and the logical command and its replay-stability
        // are unchanged from the pre-ADR-PC-043 legacy path).
        var processId = Guid.NewGuid();
        var causation = Guid.NewGuid();
        var engineCaAccount = Guid.NewGuid().ToString();
        var engineCaRef = new SagaBusinessReference(
            ProcessId: processId,
            ProductRef: "dpz_pt_12m_juros_venc",
            AmountMinorUnits: 500_000L,
            SourceAccountRef: engineCaAccount,
            InterestAccountRef: null,
            DepositRef: "DEP-1",
            ClientType: ClientType.Existing,
            AutoApprovalThresholdMinorUnits: 1_000_000L);

        var reserve = Assert.IsType<ReserveAccountBalanceCommand>(SagaCommandPayloadFactory.Build(
            ConstitutionProcess.ReserveAccountBalance, processId, causation, correlationId: null, engineCaRef));
        Assert.Equal("engine-ca", reserve.SettlementTarget);
        Assert.Equal(engineCaAccount, reserve.AccountRef);

        var confirm = Assert.IsType<ConfirmDebitCommand>(SagaCommandPayloadFactory.Build(
            ConstitutionProcess.ConfirmDebit, processId, causation, correlationId: null, engineCaRef));
        Assert.Equal("engine-ca", confirm.SettlementTarget);
        Assert.Equal(engineCaAccount, confirm.AccountRef);
        Assert.Equal(500_000L, confirm.AmountCents);
        // The confirm's hold-linking intent reference is the SAME reservation reference the reserve leg used,
        // so the engine ingress captures exactly the hold the reserve's authorize placed.
        Assert.Equal(reserve.ReservationRef, confirm.IntentReference);

        // A legacy funding account (a non-GUID opaque token) → no engine-ca extras, body unchanged.
        var legacyRef = engineCaRef with { SourceAccountRef = "acct-ref-001" };
        var legacyReserve = Assert.IsType<ReserveAccountBalanceCommand>(SagaCommandPayloadFactory.Build(
            ConstitutionProcess.ReserveAccountBalance, processId, causation, correlationId: null, legacyRef));
        Assert.Null(legacyReserve.SettlementTarget);
        var legacyConfirm = Assert.IsType<ConfirmDebitCommand>(SagaCommandPayloadFactory.Build(
            ConstitutionProcess.ConfirmDebit, processId, causation, correlationId: null, legacyRef));
        Assert.Null(legacyConfirm.SettlementTarget);
        Assert.Null(legacyConfirm.AccountRef);
        Assert.Null(legacyConfirm.AmountCents);
        Assert.Null(legacyConfirm.IntentReference);
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
            new RecordingCommandSink()));
    }

    [Fact]
    public void SagaAdvanceHandler_rejects_an_empty_machine_set()
    {
        Assert.Throws<ArgumentException>(() => new SagaAdvanceHandler(
            Array.Empty<ISagaStateMachine>(),
            new SagaStateStore(), new SagaTransitionLog(),
            new RecordingCommandSink()));
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
            new RecordingCommandSink());

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
                _stateStore, _transitionLog, sink);

            // Seed one saga of EACH type directly in STARTED (the routing proof needs only a row with a
            // saga_type — not the full edge-start ceremony, which is ConstitutionProcess-specific).
            var constitutionId = await StartRowAsync(ConstitutionProcess.Type);
            var renewalId = await StartRowAsync(StubRenewalProcess.Type);

            // Advance the constitution saga on ITS event → its constitution edge (PARALLEL_VALIDATION).
            Assert.Equal(AdvanceOutcome.Advanced,
                await RunAsync(handler, constitutionId, ConstitutionProcess.ConstitutionRequested));
            Assert.Equal(ConstitutionProcess.States.ParallelValidation, await StateAsync(constitutionId));

            // Advance the renewal saga on ITS event → its renewal edge (Approved). SAME handler, routed
            // to the OTHER machine purely by saga_type.
            Assert.Equal(AdvanceOutcome.Advanced,
                await RunAsync(handler, renewalId, StubRenewalProcess.RenewalRequested));
            Assert.Equal(ConstitutionProcess.States.Approved, await StateAsync(renewalId));

            // Cross-vocabulary events are NoTransition (the other machine has no such edge), NOT applied
            // to the wrong machine: the renewal event is illegal for the constitution saga and vice versa.
            Assert.Equal(AdvanceOutcome.NoTransition,
                await RunAsync(handler, constitutionId, StubRenewalProcess.RenewalRequested));
            Assert.Equal(AdvanceOutcome.NoTransition,
                await RunAsync(handler, renewalId, ConstitutionProcess.ConstitutionRequested));

            // The renewal saga completes on ITS terminal edge — and IsTerminal then short-circuits.
            Assert.Equal(AdvanceOutcome.Advanced,
                await RunAsync(handler, renewalId, StubRenewalProcess.RenewalCompleted));
            Assert.Equal(ConstitutionProcess.States.Completed, await StateAsync(renewalId));
            // A late event for the now-terminal renewal saga is a no-op advance (per-machine IsTerminal).
            Assert.Equal(AdvanceOutcome.Terminal,
                await RunAsync(handler, renewalId, StubRenewalProcess.RenewalCompleted));
        }

        /// <summary>
        /// The behaviour-preservation lock for the HUMAN_INTERVENTION_REQUIRED disposition
        /// (bd babelstone-mtto PR1). Before the multi-saga refactor the advance handler asked
        /// <see cref="SagaStateNames.IsTerminal"/>, which keeps HIR NON-terminal, so a late event on a
        /// HIR-parked ConstitutionProcess saga fell through to the table lookup, found no <c>(HIR, *)</c>
        /// row, and returned <see cref="AdvanceOutcome.NoTransition"/> (which the consume loop counts as
        /// poison). The refactor switched the handler to <see cref="ISagaStateMachine.IsTerminal"/>; the
        /// substrate DEFAULT (pure table inspection) would have reported HIR terminal (no outgoing edge)
        /// and changed the disposition to <see cref="AdvanceOutcome.Terminal"/> — a different inbox
        /// result_summary and a different metric, so replay would diverge from live. ConstitutionProcess
        /// overrides IsTerminal to delegate to the static, restoring the pre-PR1 NoTransition disposition.
        /// This test drives exactly that case and fails the moment the override is dropped.
        /// </summary>
        [Fact]
        public async Task A_late_event_on_a_HIR_parked_constitution_saga_is_NoTransition_not_Terminal()
        {
            var sink = new RecordingCommandSink();
            var handler = new SagaAdvanceHandler(
                new ISagaStateMachine[] { new ConstitutionProcess(), new StubRenewalProcess() },
                _stateStore, _transitionLog, sink);

            // Seed a ConstitutionProcess saga PARKED in HUMAN_INTERVENTION_REQUIRED — the
            // production-reachable escalation state (a failed compensation / spent reissue budget land
            // here). HIR has no outgoing edge in the table TODAY (the operator-resolution edge is PR2),
            // so the substrate default would call it terminal; the override keeps it non-terminal.
            var hirSagaId = await StartRowAsync(ConstitutionProcess.Type, ConstitutionProcess.States.HumanInterventionRequired);

            // A late event for that saga must take the NoTransition path (the pre-PR1 disposition), NOT
            // the Terminal short-circuit: the routed machine reports HIR non-terminal, the table has no
            // (HIR, *) row, so it is a structurally-impossible advance the consume loop routes to poison.
            Assert.Equal(AdvanceOutcome.NoTransition,
                await RunAsync(handler, hirSagaId, ConstitutionProcess.CompensationFailed));

            // The saga did not move — HIR is preserved, not collapsed to a terminal.
            Assert.Equal(ConstitutionProcess.States.HumanInterventionRequired, await StateAsync(hirSagaId));
        }

        [Fact]
        public async Task An_unregistered_saga_type_fails_closed()
        {
            var sink = new RecordingCommandSink();
            // A handler that knows ONLY the renewal machine.
            var handler = new SagaAdvanceHandler(
                new ISagaStateMachine[] { new StubRenewalProcess() },
                _stateStore, _transitionLog, sink);

            // A saga row whose saga_type has no registered machine: the advance must fail-closed (throw),
            // never silently skip — the substrate cannot decide a saga it has no machine for.
            var orphanId = await StartRowAsync("UnregisteredSaga");
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                RunAsync(handler, orphanId, "AnyEvent"));
        }

        // ---- helpers ----------------------------------------------------------------------------

        private Task<Guid> StartRowAsync(string sagaType) => StartRowAsync(sagaType, ConstitutionProcess.States.Started);

        private async Task<Guid> StartRowAsync(string sagaType, string initialState)
        {
            var processId = Guid.NewGuid();
            await using var connection = await OpenAsync();
            await using var tx = await connection.BeginTransactionAsync();
            var created = await _stateStore.TryStartAsync(
                connection, tx, processId, subjectId: processId, sagaType, initialState, correlationId: null);
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

        private async Task<string> StateAsync(Guid processId)
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

    // ---- DB-backed: the EVENT-AUTO-START guard against an empty-subject relay record ----------------

    /// <summary>
    /// The defensive-depth proof for the EVENT-AUTO-START branch (ADR-IC-018 §P5; bd babelstone-mtto.4
    /// review). A renewal saga is BORN on a <c>DepositMatured</c> bus fact whose process id is the closing
    /// deposit's <c>ce_subject</c>. The consume loop falls back to <see cref="Guid.Empty"/> when a record's
    /// <c>ce_subject</c> is absent or unparseable (the intended UnknownSaga reject for an edge saga). The
    /// engine relay ALWAYS stamps <c>ce_subject = aggregate_id</c>, so a missing/garbled subject is a
    /// producer defect — and a malformed relay record must NEVER mint an empty-keyed renewal instance. This
    /// asserts the guard: an auto-start-eligible event (the policy predicate would pass) with a
    /// <see cref="Guid.Empty"/> process id is rejected as <see cref="AdvanceOutcome.UnknownSaga"/> and
    /// creates NO saga row, while the SAME event with a valid non-empty subject auto-starts normally.
    /// </summary>
    [Trait("Category", "Integration")]
    [Collection(nameof(OrchestratorPostgresCollection))]
    public sealed class RenewalAutoStartEmptySubjectGuardIntegrationTests(OrchestratorPostgresFixture fixture)
    {
        private readonly SagaStateStore _stateStore = new();
        private readonly SagaTransitionLog _transitionLog = new();

        // The renewal saga's auto-start predicate keys on this extension header (autorenewalpolicy != NONE).
        private static readonly IReadOnlyDictionary<string, string> RenewablePolicyHeaders =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["autorenewalpolicy"] = "SAME_TERM_CURRENT_RATE",
            };

        [Fact]
        public async Task An_empty_subject_DepositMatured_does_NOT_mint_a_renewal_saga()
        {
            var handler = NewRenewalAutoStartHandler();

            // An auto-start-eligible DepositMatured (the policy predicate WOULD pass) but with a Guid.Empty
            // process id — what the consume loop produces from a relay record missing/garbling ce_subject.
            // The guard must reject it as UnknownSaga, never mint a Guid.Empty-keyed saga.
            var outcome = await RunAsync(handler, new SagaInboxEvent(
                Guid.NewGuid(), Guid.Empty, RenewalProcess.DepositMatured, "test.injected",
                CorrelationId: null, ExtensionHeaders: RenewablePolicyHeaders));

            Assert.Equal(AdvanceOutcome.UnknownSaga, outcome);
            // No empty-keyed saga row was created — the malformed record cannot mint a renewal instance.
            Assert.Null(await StateOrNullAsync(Guid.Empty));
        }

        [Fact]
        public async Task A_valid_subject_DepositMatured_auto_starts_the_renewal_saga()
        {
            // The positive control: the SAME event with a real (non-empty) ce_subject DOES auto-start, so the
            // empty-subject guard is the only thing the negative test removes — not the auto-start path itself.
            var handler = NewRenewalAutoStartHandler();
            var closingDepositId = Guid.NewGuid();

            var outcome = await RunAsync(handler, new SagaInboxEvent(
                Guid.NewGuid(), closingDepositId, RenewalProcess.DepositMatured, "test.injected",
                CorrelationId: null, ExtensionHeaders: RenewablePolicyHeaders));

            Assert.Equal(AdvanceOutcome.Advanced, outcome);
            // The saga was born and took its first edge → RENEWAL_CONSTITUTING.
            Assert.Equal(RenewalProcess.States.RenewalConstituting, await StateOrNullAsync(closingDepositId));
        }

        // ---- helpers ----------------------------------------------------------------------------

        private SagaAdvanceHandler NewRenewalAutoStartHandler()
        {
            // The real RenewalProcess machine + its module (so the substrate's auto-start registry is built
            // from the module's declared AutoStartRule + header predicate). A bare recording sink is enough —
            // the negative case emits no command; the positive case emits ConstituteRenewal, which the
            // recording sink absorbs without a typed route.
            var context = new SagaModuleContext(
                RuntimeConnectionString: fixture.ConnectionString,
                EngineBaseUrl: "http://engine.invalid",
                SettlementBaseUrl: "http://settlement.invalid");
            var module = new RenewalSagaModule(context);
            return new SagaAdvanceHandler(
                new ISagaStateMachine[] { module.StateMachine },
                _stateStore, _transitionLog, new RecordingCommandSink(),
                new ISagaModule[] { module });
        }

        private async Task<AdvanceOutcome> RunAsync(SagaAdvanceHandler handler, SagaInboxEvent message)
        {
            await using var connection = new NpgsqlConnection(fixture.ConnectionString);
            await connection.OpenAsync();
            await using var tx = await connection.BeginTransactionAsync();
            var outcome = await handler.AdvanceAsync(connection, tx, message);
            await tx.CommitAsync();
            return outcome;
        }

        private async Task<string?> StateOrNullAsync(Guid processId)
        {
            await using var connection = new NpgsqlConnection(fixture.ConnectionString);
            await connection.OpenAsync();
            await using var tx = await connection.BeginTransactionAsync();
            var saga = await _stateStore.LoadAsync(connection, tx, processId);
            await tx.RollbackAsync();
            return saga?.State;
        }
    }
}
