using Babelstone.Orchestrator.Dispatch;
using Babelstone.Orchestrator.Saga;
using Babelstone.Orchestrator.Saga.Settlement;
using Xunit;

namespace Babelstone.Orchestrator.Tests;

/// <summary>
/// The PRODUCER↔CONSUMER contract for the Movement-routing CloudEvents headers (ADR-PC-032 §A7/§A8;
/// ADR-IC-018 §P5/§D5; bd babelstone-t7o3.20). In plain English: the engine spine promotes a Movement-bearing
/// event's origin/direction to the <c>ce_movementorigin</c> / <c>ce_movementdirections</c> headers (its
/// producer, <c>Babelstone.Engine.MovementHeaders</c>); the substrate-owned settlement saga reads those SAME
/// header values to auto-start and to pick the debit/credit branch (its consumer, the
/// <see cref="SettlementSagaModule"/> auto-start predicate + the <see cref="SettlementProcess"/> substitutor).
/// These tests pin that the LITERAL wire values the producer emits (<c>Originated</c> / <c>Debit</c> /
/// <c>Credit</c> — the <see cref="Babelstone.Engine.MovementOrigin"/> / <c>SettlementDirection</c> enum member
/// names) are exactly the strings the consumer matches on, so the two halves agree across the bus.
/// </summary>
/// <remarks>
/// The orchestrator substrate deliberately depends only on <c>Confluent.Kafka</c>, NOT on the engine kernel
/// (ADR-PC-019 §P2 — the saga reasons over a PII-free header projection, never the Avro payload). So these
/// tests assert against the LITERAL header strings the producer emits, which is the right boundary: the wire
/// contract is the agreement, not a shared type. The producer's own unit tests
/// (<c>MovementHeadersTests</c>) prove those literals are the enum member names; these prove the consumer
/// matches them.
/// </remarks>
public sealed class MovementHeaderAutoStartContractTests
{
    // The exact wire strings the engine-spine producer (Babelstone.Engine.MovementHeaders) emits — the
    // MovementOrigin / SettlementDirection enum member names. Duplicated here as literals on purpose: the
    // substrate does not reference the engine, so the WIRE VALUE is the contract under test.
    private const string OriginatedValue = "Originated";
    private const string DebitValue = "Debit";
    private const string CreditValue = "Credit";

    // The ce_settlementtarget counterparty wire values the engine promotes (ADR-PC-043 slot 1) — again
    // duplicated as literals because the substrate does not reference the engine (ADR-PC-019 §P2). The engine
    // producer's own tests pin these are its SettlementTarget enum's wire tokens; this pins the substrate
    // router's SettlementCommandRouter constants match them, so the two halves agree across the bus.
    private const string SettlementTargetKey = "settlementtarget";
    private const string EngineCaValue = "engine-ca";
    private const string LegacyDdaValue = "legacy-dda";

    private static readonly SettlementSagaModule Module = new(
        new SagaModuleContext(
            RuntimeConnectionString: "Host=x;Database=y",
            EngineBaseUrl: "http://engine.invalid",
            SettlementBaseUrl: "http://acl.invalid"),
        consumeTopics: ["personal_loan"]);

    [Fact]
    public void The_engine_ca_destination_and_amount_header_keys_are_the_pinned_wire_literals()
    {
        // ADR-PC-043 §D5: the engine promotes the per-movement destination + amount as movementaccountrefs /
        // movementamounts on an engine-CA leg; the payload-blind substrate reads those SAME keys. The
        // orchestrator is extraction-ready (ADR-PC-019 §P2) — it CANNOT reference the engine constant, so it
        // pins its OWN literal here against the wire string. The engine pins the SAME wire string on the producer
        // side (Babelstone.Engine.Tests.MovementHeadersTests) — the two halves agree on the wire literal, so a
        // rename on ONE side and not the other fails one of the two pins (the settlementtarget contract pattern).
        Assert.Equal("movementaccountrefs", SettlementMovementFanout.AccountRefsHeader);
        Assert.Equal("movementamounts", SettlementMovementFanout.AmountsHeader);
    }

    [Fact]
    public void The_auto_start_predicate_fires_on_the_producer_emitted_origin_value()
    {
        // The settlement saga is born when movementorigin == Originated (the producer's
        // MovementOrigin.Originated.ToString()). The predicate reads the extension-attribute map the consume
        // loop projects (ce_-stripped, lowercased keys), so it keys on "movementorigin".
        var rule = Module.AutoStartRule;
        Assert.NotNull(rule);

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [SettlementSagaModule.OriginHeader] = OriginatedValue,
            [SettlementMovementFanout.DirectionsHeader] = DebitValue,
        };
        Assert.True(rule!.HeaderPredicate!(headers));
    }

    [Fact]
    public void The_auto_start_predicate_does_not_fire_on_an_observed_or_absent_origin()
    {
        var rule = Module.AutoStartRule;

        // Observed = no cash leg to drive (slot 2) → no settlement saga. The producer never emits the
        // movement headers for an Observed-only event, but assert the predicate fail-closes even if it did.
        var observed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [SettlementSagaModule.OriginHeader] = "Observed",
        };
        Assert.False(rule!.HeaderPredicate!(observed));

        // No movementorigin header at all (a non-Movement event) → no start.
        Assert.False(rule.HeaderPredicate!(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)));
    }

    [Theory]
    [InlineData(DebitValue, SettlementProcess.DebitMovementOriginated)]
    [InlineData(CreditValue, SettlementProcess.CreditMovementOriginated)]
    public async Task The_substitutor_resolves_the_branch_from_the_producer_emitted_direction_value(
        string producerDirection, string expectedEffectiveEvent)
    {
        // After auto-start (and fan-out, which reduces each leg to its single direction), the machine's
        // substitutor maps the generic start event to the debit or credit branch from the leg's single-entry
        // movementdirections list — the SAME wire value the producer emits (SettlementDirection.Debit/Credit
        // .ToString()).
        var machine = new SettlementProcess();
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [SettlementMovementFanout.DirectionsHeader] = producerDirection,
        };

        var effective = await machine.SubstituteAsync(
            SettlementProcess.States.SettlementStarted, SettlementProcess.MovementOriginated,
            transitionLog: null!, connection: null!, transaction: null!,
            processId: Guid.NewGuid(), extensionHeaders: headers, ct: default);

        Assert.Equal(expectedEffectiveEvent, effective);

        // And the resolved branch is a real transition out of SETTLEMENT_STARTED (the producer's value drives
        // the saga forward, not a dead end).
        Assert.True(machine.TryAdvance(SettlementProcess.States.SettlementStarted, effective, out _));
    }

    [Fact]
    public void The_router_keys_the_counterparty_on_the_producer_emitted_settlement_target_values()
    {
        // The ce_settlementtarget producer↔consumer contract (ADR-PC-043 slots 1-2): the router's wire literals
        // are exactly the tokens the engine's MovementHeaders promotes. First pin the substrate's own constants
        // agree with the producer's wire strings (the engine's tests pin the producer side).
        Assert.Equal(SettlementTargetKey, SettlementCommandRouter.SettlementTargetHeader);
        Assert.Equal(EngineCaValue, SettlementCommandRouter.EngineCaValue);
        Assert.Equal(LegacyDdaValue, SettlementCommandRouter.LegacyDdaValue);

        // Then prove the router actually routes on the producer-emitted value — engine-ca diverts to the
        // engine-CA base URL, legacy-dda / absent stays on the legacy one (header-only, never the payload).
        var options = new SagaCommandDispatcherOptions
        {
            ConnectionString = "Host=x;Database=y",
            EngineBaseUrl = "http://engine.invalid",
            SettlementBaseUrl = "http://acl.legacy",
            EngineCaSettlementBaseUrl = "http://engine-ca.test",
        };
        var router = new SettlementCommandRouter(options);

        var engineCa = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [SettlementTargetKey] = EngineCaValue,
        };
        Assert.Equal("http://engine-ca.test",
            router.Resolve(SettlementProcess.ConfirmCredit, engineCa)!.BaseUrl);
        Assert.Equal("http://acl.legacy", router.Resolve(SettlementProcess.ConfirmCredit)!.BaseUrl);
    }
}
