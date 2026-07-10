using Babelstone.Orchestrator.Dispatch;
using Babelstone.Orchestrator.Saga;
using Babelstone.Orchestrator.Saga.Settlement;
using Xunit;

namespace Babelstone.Orchestrator.Tests;

/// <summary>
/// Pure tests of the substrate-owned <see cref="SettlementProcess"/> state machine + its result-event
/// bridge + its direction-resolving substitutor + its command router (bd babelstone-t7o3.15, ADR-PC-032)
/// — the ADR-IC-003 §P2 "the state machine is the specification" fitness function for the settlement saga.
/// No clock, no I/O, no DB: the transition table and the bridge map are pure data structures and these
/// assert their shape directly (the table IS the documentation; these prove it).
/// </summary>
public sealed class SettlementProcessSagaTests
{
    private readonly SettlementProcess _machine = new();

    [Fact]
    public void Starts_in_SETTLEMENT_STARTED_and_is_substrate_owned_family_agnostic()
    {
        Assert.Equal(SettlementProcess.States.SettlementStarted, _machine.InitialState);
        Assert.Equal(SettlementProcess.Type, _machine.SagaType);
        Assert.Equal("SettlementProcess", SettlementProcess.Type);
    }

    // ---- Debit path: funds-gated Reserve -> Confirm (ADR-PC-016 slot 5) -------------------------------

    [Fact]
    public void Debit_happy_path_reserves_then_confirms_then_completes()
    {
        // A debit Movement is funds-gated: auto-started -> ReserveAccountBalance (reversible hold);
        // BalanceReserved -> ConfirmDebit (irreversible, only AFTER the reserve, §P5); DebitConfirmed -> done.
        AssertTransition(SettlementProcess.States.SettlementStarted, SettlementProcess.DebitMovementOriginated,
            SettlementProcess.States.Reserving, SettlementProcess.ReserveAccountBalance);
        AssertTransition(SettlementProcess.States.Reserving, SettlementProcess.BalanceReserved,
            SettlementProcess.States.ConfirmingDebit, SettlementProcess.ConfirmDebit);
        AssertTransition(SettlementProcess.States.ConfirmingDebit, SettlementProcess.DebitConfirmed,
            SettlementProcess.States.SettlementCompleted);
    }

    [Fact]
    public void A_refused_reserve_parks_in_HIR_with_no_compensation_command()
    {
        // No hold was placed, so there is nothing to release: park fail-closed in HIR, emit NO reversal
        // command (ADR-IC-003 §P6 — the cash never moved; the saga never invents an undo).
        AssertTransition(SettlementProcess.States.Reserving, SettlementProcess.ReserveRefused,
            SettlementProcess.States.HumanInterventionRequired);
    }

    [Fact]
    public void An_indeterminate_debit_enters_clearance_and_resolves_both_ways()
    {
        // 202 INDETERMINATE -> park in the first-class wait + emit the clearance query (never blind-retry).
        AssertTransition(SettlementProcess.States.ConfirmingDebit, SettlementProcess.DebitIndeterminate,
            SettlementProcess.States.AwaitDebitClearance, SettlementProcess.QueryCoreDebitStatus);
        // EXECUTED -> the debit DID land (late confirm) -> done.
        AssertTransition(SettlementProcess.States.AwaitDebitClearance, SettlementProcess.DebitClearedExecuted,
            SettlementProcess.States.SettlementCompleted);
        // NOT executed -> RETRY_PERMITTED reissue of the debit (same idempotency key; cannot double-debit).
        AssertTransition(SettlementProcess.States.AwaitDebitClearance, SettlementProcess.DebitClearedNotExecuted,
            SettlementProcess.States.ConfirmingDebit, SettlementProcess.ConfirmDebit);
        // A clearance that cannot resolve -> escalate, never strand (§P6).
        AssertTransition(SettlementProcess.States.AwaitDebitClearance, SettlementProcess.ClearanceFailed,
            SettlementProcess.States.HumanInterventionRequired);
    }

    // ---- Credit path: confirmation-gated only, with the credit-clearance path (feature-design §10) ----

    [Fact]
    public void Credit_happy_path_confirms_then_completes_with_no_reserve_leg()
    {
        // A credit Movement is confirmation-gated only: auto-started -> ConfirmCredit (NO reserve leg);
        // CreditConfirmed -> done. The legacy Core always accepts a credit, but it must confirm.
        AssertTransition(SettlementProcess.States.SettlementStarted, SettlementProcess.CreditMovementOriginated,
            SettlementProcess.States.ConfirmingCredit, SettlementProcess.ConfirmCredit);
        AssertTransition(SettlementProcess.States.ConfirmingCredit, SettlementProcess.CreditConfirmed,
            SettlementProcess.States.SettlementCompleted);
        // The credit path has NO reserve leg — a reserve-related event has no transition out of the credit
        // confirm state (the funds-gated asymmetry of ADR-PC-016 slot 5).
        Assert.False(_machine.TryAdvance(
            SettlementProcess.States.SettlementStarted, SettlementProcess.ReserveAccountBalance, out _));
    }

    [Fact]
    public void An_indeterminate_credit_enters_clearance_NEVER_silent_and_resolves_both_ways()
    {
        // The NEW credit-clearance surface (feature-design §10): a non-confirmed credit enters clearance,
        // NEVER silent. 202 INDETERMINATE -> park + emit the credit clearance query.
        AssertTransition(SettlementProcess.States.ConfirmingCredit, SettlementProcess.CreditIndeterminate,
            SettlementProcess.States.AwaitCreditClearance, SettlementProcess.QueryCoreCreditStatus);
        // EXECUTED -> the credit DID land (late confirm) -> done.
        AssertTransition(SettlementProcess.States.AwaitCreditClearance, SettlementProcess.CreditClearedExecuted,
            SettlementProcess.States.SettlementCompleted);
        // NOT executed -> RETRY_PERMITTED reissue of the credit.
        AssertTransition(SettlementProcess.States.AwaitCreditClearance, SettlementProcess.CreditClearedNotExecuted,
            SettlementProcess.States.ConfirmingCredit, SettlementProcess.ConfirmCredit);
        AssertTransition(SettlementProcess.States.AwaitCreditClearance, SettlementProcess.ClearanceFailed,
            SettlementProcess.States.HumanInterventionRequired);
    }

    // ---- Terminal model (ADR-IC-003 §P6 — park, never compensate) -------------------------------------

    [Fact]
    public void HIR_is_NON_terminal_by_table_and_an_operator_resolves_it_to_SETTLEMENT_COMPLETED()
    {
        // HIR has an OUTGOING OperatorResolved edge, so the substrate default TableStateMachine.IsTerminal
        // (pure table inspection) ALREADY reports it non-terminal — NO IsTerminal override needed (the
        // RenewalProcess posture, not ConstitutionProcess's override-needed one).
        Assert.False(_machine.IsTerminal(SettlementProcess.States.HumanInterventionRequired));
        AssertTransition(SettlementProcess.States.HumanInterventionRequired, SettlementProcess.OperatorResolved,
            SettlementProcess.States.SettlementCompleted);
    }

    [Fact]
    public void SETTLEMENT_COMPLETED_is_terminal_and_in_flight_states_are_not()
    {
        Assert.True(_machine.IsTerminal(SettlementProcess.States.SettlementCompleted));
        Assert.True(SettlementProcess.States.IsTerminal(SettlementProcess.States.SettlementCompleted));
        Assert.False(_machine.IsTerminal(SettlementProcess.States.SettlementStarted));
        Assert.False(_machine.IsTerminal(SettlementProcess.States.Reserving));
        Assert.False(_machine.IsTerminal(SettlementProcess.States.ConfirmingDebit));
        Assert.False(_machine.IsTerminal(SettlementProcess.States.ConfirmingCredit));
        Assert.False(_machine.IsTerminal(SettlementProcess.States.AwaitDebitClearance));
        Assert.False(_machine.IsTerminal(SettlementProcess.States.AwaitCreditClearance));
        Assert.False(SettlementProcess.States.IsTerminal(SettlementProcess.States.HumanInterventionRequired));
    }

    [Fact]
    public void Illegal_pairs_are_rejected_never_silently_applied()
    {
        // An event with no (state, event) row is rejected (ADR-IC-003 §P2). The generic start event is NOT
        // directly in the table — it is substituted to a direction-specific event first; a raw
        // MovementOriginated has no transition out of SETTLEMENT_STARTED.
        Assert.False(_machine.TryAdvance(
            SettlementProcess.States.SettlementStarted, SettlementProcess.MovementOriginated, out _));
        // A credit event has no transition on the debit path, and vice versa.
        Assert.False(_machine.TryAdvance(SettlementProcess.States.Reserving, SettlementProcess.CreditConfirmed, out _));
        Assert.False(_machine.TryAdvance(SettlementProcess.States.ConfirmingCredit, SettlementProcess.BalanceReserved, out _));
    }

    // ---- The direction-resolving substitutor (the header-only branch, ADR-IC-018 §D5) ----------------

    [Theory]
    [InlineData("Debit", SettlementProcess.DebitMovementOriginated)]
    [InlineData("Credit", SettlementProcess.CreditMovementOriginated)]
    public async Task The_start_event_resolves_to_a_direction_branch_from_the_movementdirections_header(
        string direction, string expectedEffectiveEvent)
    {
        // The substitutor reads ONLY the leg's single-entry ce_movementdirections list (never the payload,
        // §D5) and maps the generic MovementOriginated start event to the debit or credit branch the table
        // drives.
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [SettlementMovementFanout.DirectionsHeader] = direction,
        };

        var effective = await _machine.SubstituteAsync(
            SettlementProcess.States.SettlementStarted, SettlementProcess.MovementOriginated,
            transitionLog: null!, connection: null!, transaction: null!,
            processId: Guid.NewGuid(), extensionHeaders: headers, ct: default);

        Assert.Equal(expectedEffectiveEvent, effective);
    }

    [Fact]
    public async Task An_absent_unknown_or_unfanned_multi_direction_header_leaves_the_unstartable_event_unchanged()
    {
        // No/unknown direction -> the un-substituted start event, which has NO transition out of
        // SETTLEMENT_STARTED -> the advance handler rejects it as NoTransition (fail-closed, never a guess).
        var unchanged = await _machine.SubstituteAsync(
            SettlementProcess.States.SettlementStarted, SettlementProcess.MovementOriginated,
            transitionLog: null!, connection: null!, transaction: null!,
            processId: Guid.NewGuid(), extensionHeaders: null, ct: default);
        Assert.Equal(SettlementProcess.MovementOriginated, unchanged);

        var unknownDir = new Dictionary<string, string> { [SettlementMovementFanout.DirectionsHeader] = "Sideways" };
        var stillUnchanged = await _machine.SubstituteAsync(
            SettlementProcess.States.SettlementStarted, SettlementProcess.MovementOriginated,
            transitionLog: null!, connection: null!, transaction: null!,
            processId: Guid.NewGuid(), extensionHeaders: unknownDir, ct: default);
        Assert.Equal(SettlementProcess.MovementOriginated, stillUnchanged);

        // A still-multi-entry list (one a fan-out should have split, but didn't) is NOT resolved to the first
        // entry — it fail-closes to the unstartable event, never a guessed/partial settlement.
        var unfanned = new Dictionary<string, string> { [SettlementMovementFanout.DirectionsHeader] = "Debit,Credit" };
        var multiUnchanged = await _machine.SubstituteAsync(
            SettlementProcess.States.SettlementStarted, SettlementProcess.MovementOriginated,
            transitionLog: null!, connection: null!, transaction: null!,
            processId: Guid.NewGuid(), extensionHeaders: unfanned, ct: default);
        Assert.Equal(SettlementProcess.MovementOriginated, multiUnchanged);

        // A non-start event is returned unchanged regardless of headers.
        var nonStart = await _machine.SubstituteAsync(
            SettlementProcess.States.ConfirmingDebit, SettlementProcess.DebitConfirmed,
            transitionLog: null!, connection: null!, transaction: null!,
            processId: Guid.NewGuid(), extensionHeaders: null, ct: default);
        Assert.Equal(SettlementProcess.DebitConfirmed, nonStart);
    }

    // ---- The result-event bridge (synthesized from command outcomes; 202 -> Indeterminate) -----------

    [Fact]
    public void Bridge_synthesizes_the_debit_path_signals()
    {
        Assert.Equal(SettlementProcess.BalanceReserved,
            SettlementResultEvents.ForOutcome(SettlementProcess.ReserveAccountBalance, CommandDeliveryKind.Applied));
        Assert.Equal(SettlementProcess.ReserveRefused,
            SettlementResultEvents.ForOutcome(SettlementProcess.ReserveAccountBalance, CommandDeliveryKind.Refused));
        Assert.Equal(SettlementProcess.DebitConfirmed,
            SettlementResultEvents.ForOutcome(SettlementProcess.ConfirmDebit, CommandDeliveryKind.Applied));
        Assert.Equal(SettlementProcess.DebitIndeterminate,
            SettlementResultEvents.ForOutcome(SettlementProcess.ConfirmDebit, CommandDeliveryKind.Indeterminate));
        Assert.Equal(SettlementProcess.DebitClearedExecuted,
            SettlementResultEvents.ForOutcome(SettlementProcess.QueryCoreDebitStatus, CommandDeliveryKind.Applied));
        Assert.Equal(SettlementProcess.DebitClearedNotExecuted,
            SettlementResultEvents.ForOutcome(SettlementProcess.QueryCoreDebitStatus, CommandDeliveryKind.Refused));
    }

    [Fact]
    public void Bridge_synthesizes_the_credit_path_signals_including_clearance()
    {
        Assert.Equal(SettlementProcess.CreditConfirmed,
            SettlementResultEvents.ForOutcome(SettlementProcess.ConfirmCredit, CommandDeliveryKind.Applied));
        Assert.Equal(SettlementProcess.CreditIndeterminate,
            SettlementResultEvents.ForOutcome(SettlementProcess.ConfirmCredit, CommandDeliveryKind.Indeterminate));
        Assert.Equal(SettlementProcess.CreditClearedExecuted,
            SettlementResultEvents.ForOutcome(SettlementProcess.QueryCoreCreditStatus, CommandDeliveryKind.Applied));
        Assert.Equal(SettlementProcess.CreditClearedNotExecuted,
            SettlementResultEvents.ForOutcome(SettlementProcess.QueryCoreCreditStatus, CommandDeliveryKind.Refused));
    }

    [Fact]
    public void Bridge_reads_a_202_on_EITHER_confirm_as_INDETERMINATE_only()
    {
        var bridge = new SettlementResultEvents.Bridge();
        Assert.Equal(SettlementProcess.Type, bridge.SagaType);
        // 202 on either confirm -> Indeterminate (the indeterminate-clearance path).
        Assert.Equal(CommandDeliveryKind.Indeterminate, bridge.ClassifyResponse(SettlementProcess.ConfirmDebit, 202));
        Assert.Equal(CommandDeliveryKind.Indeterminate, bridge.ClassifyResponse(SettlementProcess.ConfirmCredit, 202));
        // A 200 confirm or a 202 on a non-confirm is NOT reinterpreted (the default classification applies).
        Assert.Null(bridge.ClassifyResponse(SettlementProcess.ConfirmDebit, 200));
        Assert.Null(bridge.ClassifyResponse(SettlementProcess.ReserveAccountBalance, 202));
        // No no-route auto-pass: every settlement command has a real ACL route.
        Assert.False(bridge.IsNoRouteAutoPass(SettlementProcess.ConfirmCredit));
        // An unmapped (command, kind) drives no advance (a graceful no-op).
        Assert.Null(bridge.ForOutcome("UnknownCommand", CommandDeliveryKind.Applied));
    }

    // ---- The command router (settlement legs -> the Core ACL, incl. the NEW credit routes) ------------

    [Fact]
    public void Router_maps_every_settlement_command_to_the_settlement_base_url()
    {
        var options = new SagaCommandDispatcherOptions
        {
            ConnectionString = "Host=x;Database=y",
            EngineBaseUrl = "http://engine.invalid",
            SettlementBaseUrl = "http://acl.test",
        };
        var router = new SettlementCommandRouter(options);
        Assert.Equal(SettlementProcess.Type, router.SagaType);

        AssertRoute(router, SettlementProcess.ReserveAccountBalance, "http://acl.test", "/v1/reservations");
        AssertRoute(router, SettlementProcess.ConfirmDebit, "http://acl.test", "/v1/debits");
        AssertRoute(router, SettlementProcess.QueryCoreDebitStatus, "http://acl.test", "/v1/debits/clearance");
        // The NEW credit routes (ADR-PC-032 / feature-design §8).
        AssertRoute(router, SettlementProcess.ConfirmCredit, "http://acl.test", "/v1/credits");
        AssertRoute(router, SettlementProcess.QueryCoreCreditStatus, "http://acl.test", "/v1/credits/clearance");
        // An unknown command has no route — a terminal routing failure, never a guess.
        Assert.Null(router.Resolve("UnknownCommand"));
        // NO settlement command ever targets the engine (every leg is a Core-ACL leg).
        foreach (var cmd in new[]
        {
            SettlementProcess.ReserveAccountBalance, SettlementProcess.ConfirmDebit,
            SettlementProcess.ConfirmCredit, SettlementProcess.QueryCoreDebitStatus,
            SettlementProcess.QueryCoreCreditStatus,
        })
        {
            Assert.Equal("http://acl.test", router.Resolve(cmd)!.BaseUrl);
        }
    }

    // ---- The counterparty router (ADR-PC-043 slots 1-2 — route by ce_settlementtarget, header-only) ----

    [Fact]
    public void Router_routes_by_the_settlement_target_header_alone_never_the_payload()
    {
        var options = new SagaCommandDispatcherOptions
        {
            ConnectionString = "Host=x;Database=y",
            EngineBaseUrl = "http://engine.invalid",
            SettlementBaseUrl = "http://acl.legacy",
            EngineCaSettlementBaseUrl = "http://engine-ca.test",
        };
        var router = new SettlementCommandRouter(options);

        // engine-ca on the header → the engine-CA base URL; the PATH is counterparty-invariant.
        var engineCa = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [SettlementCommandRouter.SettlementTargetHeader] = SettlementCommandRouter.EngineCaValue,
        };
        var caRoute = router.Resolve(SettlementProcess.ConfirmCredit, engineCa);
        Assert.NotNull(caRoute);
        Assert.Equal("http://engine-ca.test", caRoute!.BaseUrl);
        Assert.Equal("/v1/credits", caRoute.Path);

        // legacy-dda on the header → the legacy counterparty (the SAME path).
        var legacy = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [SettlementCommandRouter.SettlementTargetHeader] = SettlementCommandRouter.LegacyDdaValue,
        };
        var legacyRoute = router.Resolve(SettlementProcess.ConfirmCredit, legacy);
        Assert.NotNull(legacyRoute);
        Assert.Equal("http://acl.legacy", legacyRoute!.BaseUrl);
        Assert.Equal("/v1/credits", legacyRoute.Path);

        // The router reads ONLY settlementtarget — an AccountRef-like body hint in the headers is IGNORED
        // (ADR-IC-018 §D5: never Movement.AccountRef). A header carrying an engine-CA-ish account ref but a
        // legacy target still routes legacy.
        var payloadHint = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [SettlementCommandRouter.SettlementTargetHeader] = SettlementCommandRouter.LegacyDdaValue,
            ["accountref"] = "ACCT-engine-ca-looking-token",
        };
        Assert.Equal("http://acl.legacy", router.Resolve(SettlementProcess.ConfirmCredit, payloadHint)!.BaseUrl);
    }

    [Fact]
    public void Router_defaults_to_legacy_when_no_settlement_target_header_is_present_unchanged()
    {
        // ADR-PC-043: an ABSENT settlement-target header defaults to legacy-DDA — so every pre-ADR-PC-043
        // caller (the single-arg Resolve, and the header-aware Resolve with a null/empty map) reaches the
        // SAME legacy routes it always did. Legacy routing is UNCHANGED.
        var options = new SagaCommandDispatcherOptions
        {
            ConnectionString = "Host=x;Database=y",
            EngineBaseUrl = "http://engine.invalid",
            SettlementBaseUrl = "http://acl.legacy",
            EngineCaSettlementBaseUrl = "http://engine-ca.test",
        };
        var router = new SettlementCommandRouter(options);

        foreach (var cmd in new[]
        {
            SettlementProcess.ReserveAccountBalance, SettlementProcess.ConfirmDebit,
            SettlementProcess.ConfirmCredit, SettlementProcess.QueryCoreDebitStatus,
            SettlementProcess.QueryCoreCreditStatus,
        })
        {
            // The pre-existing single-arg overload — the legacy default.
            Assert.Equal("http://acl.legacy", router.Resolve(cmd)!.BaseUrl);
            // The header-aware overload with no header — also legacy.
            Assert.Equal("http://acl.legacy", router.Resolve(cmd, extensionHeaders: null)!.BaseUrl);
            Assert.Equal("http://acl.legacy",
                router.Resolve(cmd, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase))!.BaseUrl);
        }
    }

    [Fact]
    public void Router_fails_closed_for_an_engine_ca_leg_when_no_engine_ca_base_url_is_configured()
    {
        // An estate that has not stood up the engine-CA surface leaves EngineCaSettlementBaseUrl null. An
        // engine-CA-targeted leg then resolves to NO route (fail-closed) rather than silently settling
        // engine-CA money on the legacy core — the drain surfaces a terminal routing failure.
        var options = new SagaCommandDispatcherOptions
        {
            ConnectionString = "Host=x;Database=y",
            EngineBaseUrl = "http://engine.invalid",
            SettlementBaseUrl = "http://acl.legacy",
            // EngineCaSettlementBaseUrl deliberately unset.
        };
        var router = new SettlementCommandRouter(options);

        var engineCa = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [SettlementCommandRouter.SettlementTargetHeader] = SettlementCommandRouter.EngineCaValue,
        };
        Assert.Null(router.Resolve(SettlementProcess.ConfirmCredit, engineCa));
        // But the legacy legs still route (the unconfigured engine-CA surface does not break legacy routing).
        Assert.Equal("http://acl.legacy", router.Resolve(SettlementProcess.ConfirmCredit)!.BaseUrl);
    }

    // ---- The payload factory (byte-stable, PII-free, process-id-derived) ------------------------------

    [Fact]
    public void Payload_factory_builds_byte_stable_bodies_for_every_command()
    {
        var processId = Guid.NewGuid();
        var causation = Guid.NewGuid();
        var correlation = Guid.NewGuid();

        foreach (var cmd in new[]
        {
            SettlementProcess.ReserveAccountBalance, SettlementProcess.ConfirmDebit,
            SettlementProcess.ConfirmCredit, SettlementProcess.QueryCoreDebitStatus,
            SettlementProcess.QueryCoreCreditStatus,
        })
        {
            var first = SettlementCommandPayloadFactory.Build(cmd, processId, causation, correlation);
            var second = SettlementCommandPayloadFactory.Build(cmd, processId, causation, correlation);
            Assert.NotNull(first);
            Assert.NotNull(second);
            // Byte-stable: re-assembling the SAME logical command yields identical bytes (ADR-PC-010 §P5) —
            // the property the no-double-move guarantee rests on (a reissue presents the SAME reference).
            Assert.Equal(first!.ToBytes(), second!.ToBytes());
            Assert.Equal(cmd, first.CommandType);
        }

        // A command with no recipe returns null (the sink turns that into a fail-closed wiring error).
        Assert.Null(SettlementCommandPayloadFactory.Build("UnknownCommand", processId, causation, correlation));
    }

    private void AssertTransition(string from, string evt, string expectedNext, params string[] expectedCommands)
    {
        Assert.True(_machine.TryAdvance(from, evt, out var outcome),
            $"expected a transition for ({from}, {evt}) but the table had none");
        Assert.Equal(expectedNext, outcome.Next);
        Assert.Equal(expectedCommands, outcome.Commands);
    }

    private static void AssertRoute(SettlementCommandRouter router, string commandType, string baseUrl, string path)
    {
        var route = router.Resolve(commandType);
        Assert.NotNull(route);
        Assert.Equal(baseUrl, route!.BaseUrl);
        Assert.Equal(path, route.Path);
        Assert.Equal(HttpMethod.Post, route.Method);
    }
}
