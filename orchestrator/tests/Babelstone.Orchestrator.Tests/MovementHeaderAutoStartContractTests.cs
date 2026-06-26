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

    private static readonly SettlementSagaModule Module = new(
        new SagaModuleContext(
            RuntimeConnectionString: "Host=x;Database=y",
            EngineBaseUrl: "http://engine.invalid",
            SettlementBaseUrl: "http://acl.invalid"),
        consumeTopics: ["personal_loan"]);

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
}
