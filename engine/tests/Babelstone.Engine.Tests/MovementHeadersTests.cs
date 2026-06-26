using Babelstone.FinancialTypes;
using Xunit;

namespace Babelstone.Engine.Tests;

/// <summary>
/// Tests for the GENERIC engine-spine producer seam <see cref="MovementHeaders"/> (ADR-PC-032 §A7/§A8;
/// ADR-IC-018 §P5/§D5; bd babelstone-t7o3.20). In plain English: when a family event records that money was
/// decided, this seam turns the event's <see cref="Movement"/>(s) into the two closed-enum CloudEvents header
/// VALUES the substrate-owned settlement saga auto-starts on — without naming any family. These prove the
/// PRODUCER half of the hop the settlement saga (the consumer) was already built to read: the
/// <c>movementorigin</c> = <c>Originated</c> auto-start key and the ordered <c>movementdirections</c> list of
/// <c>Debit</c> | <c>Credit</c> the substrate fans out / branches on, the exact strings the substrate's
/// <c>SettlementSagaModule</c> / <c>SettlementProcess</c> match on. The relay-side promotion of these values to
/// <c>ce_*</c> headers is covered alongside the existing extension-attribute promotion in <c>WireFormatTests</c>.
/// </summary>
public sealed class MovementHeadersTests
{
    private static Movement Originated(SettlementDirection direction) => new(
        AccountRef: "acct-ref-opaque",
        Direction: direction,
        Amount: new Money(10_000),
        ValueDate: new DateOnly(2026, 6, 25),
        Operation: MovementOperation.Disburse,
        Origin: MovementOrigin.Originated,
        CommandId: Guid.NewGuid());

    private static Movement Observed(SettlementDirection direction) => Originated(direction) with
    {
        Origin = MovementOrigin.Observed,
    };

    [Theory]
    [InlineData(SettlementDirection.Debit, "Debit")]
    [InlineData(SettlementDirection.Credit, "Credit")]
    public void A_single_originated_movement_promotes_origin_and_a_one_entry_directions_list(
        SettlementDirection direction, string expectedDirection)
    {
        // The two routing discriminators the substrate reads off the headers (never the payload): origin
        // tells the auto-start predicate there IS a cash leg to drive (Originated), the movementdirections
        // list tells the substrate how to fan out / branch. A standalone leg is a one-entry list. The values
        // are the enum member NAMES — the SAME strings the substrate's SettlementSagaModule.OriginatedValue /
        // SettlementProcess direction values match on.
        var headers = MovementHeaders.ForOriginatedMovements([Originated(direction)]);

        Assert.NotNull(headers);
        Assert.Equal("Originated", headers![MovementHeaders.OriginKey]);
        Assert.Equal(expectedDirection, headers[MovementHeaders.DirectionsKey]);
        // No amount, no account_ref, no command_id — only the two closed-enum discriminators ride the
        // headers (ADR-PC-004 §P2 / ADR-PC-032 §A8). Those stay in the payload.
        Assert.Equal(2, headers.Count);
    }

    [Fact]
    public void Several_originated_movements_list_one_entry_per_Movement_in_carrier_order()
    {
        // ONE entry PER Movement (ADR-PC-032 §A9/§A10 "one settlement instance per Originated Movement"): two
        // debits are TWO distinct cash legs, so they list as "Debit,Debit" and the substrate fans them into
        // two settlement instances — NOT one (the prior first-direction-only scheme silently collapsed
        // same-direction movements to a single instance, dropping the second leg). One entry per Movement,
        // in carrier order.
        var headers = MovementHeaders.ForOriginatedMovements(
            [Originated(SettlementDirection.Debit), Originated(SettlementDirection.Debit)]);

        Assert.NotNull(headers);
        Assert.Equal("Originated", headers![MovementHeaders.OriginKey]);
        Assert.Equal("Debit,Debit", headers[MovementHeaders.DirectionsKey]);
        Assert.Equal(2, headers.Count);
    }

    [Fact]
    public void An_observed_only_event_promotes_no_settlement_headers()
    {
        // An Observed movement arrived already cleared (slot 2): there is no cash leg to drive, so the event
        // declares NO settlement headers and starts NO saga — the relay leaves its standard CE set untouched.
        Assert.Null(MovementHeaders.ForOriginatedMovements([Observed(SettlementDirection.Credit)]));
    }

    [Fact]
    public void A_movement_free_event_promotes_no_settlement_headers()
    {
        Assert.Null(MovementHeaders.ForOriginatedMovements([]));
    }

    [Fact]
    public void An_originated_movement_alongside_an_observed_one_lists_only_the_originated_direction()
    {
        // Only Originated movements have a cash leg; the Observed one is ignored for header purposes, so the
        // list carries the single Originated direction.
        var headers = MovementHeaders.ForOriginatedMovements(
            [Observed(SettlementDirection.Debit), Originated(SettlementDirection.Credit)]);

        Assert.NotNull(headers);
        Assert.Equal("Credit", headers![MovementHeaders.DirectionsKey]);
    }

    [Fact]
    public void An_event_with_originated_movements_in_both_directions_emits_an_ordered_directions_list()
    {
        // The multi-Movement split, RESOLVED (ADR-PC-032 §A9/§A10, option b): a renewal's rollover-debit +
        // interest-credit ride ONE event. The producer emits the ordered movementdirections list, so the
        // substrate fans the event into one settlement instance per Movement — no fail-loud, no silent loss,
        // no guessed branch.
        var headers = MovementHeaders.ForOriginatedMovements(
            [Originated(SettlementDirection.Debit), Originated(SettlementDirection.Credit)]);

        Assert.NotNull(headers);
        Assert.Equal("Originated", headers![MovementHeaders.OriginKey]);
        // movementdirections carries the ORDERED set (carrier order) the substrate fans out on.
        Assert.Equal("Debit,Credit", headers[MovementHeaders.DirectionsKey]);
        Assert.Equal(2, headers.Count);
    }

    [Fact]
    public void The_directions_list_preserves_carrier_order()
    {
        // The substrate fans out in this order and the dispatcher's per-process FIFO preserves it
        // (feature-design §6 "effects its legs in declared order"). A credit-first carrier emits Credit,Debit.
        var headers = MovementHeaders.ForOriginatedMovements(
            [Originated(SettlementDirection.Credit), Originated(SettlementDirection.Debit)]);

        Assert.NotNull(headers);
        Assert.Equal("Credit,Debit", headers![MovementHeaders.DirectionsKey]);
    }

    [Fact]
    public void A_movement_bearing_event_routes_its_IntegrationHeaders_through_the_generic_helper()
    {
        // The end-to-end PRODUCER shape: a Movement-bearing event's IntegrationHeaders override returns the
        // helper's map, so the engine relay promotes ce_movementorigin / ce_movementdirections for free,
        // family-agnostically (no family event type is named in the spine).
        var debitEvent = new MovementBearingTestEvent([Originated(SettlementDirection.Debit)]);
        Assert.NotNull(debitEvent.IntegrationHeaders);
        Assert.Equal("Originated", debitEvent.IntegrationHeaders![MovementHeaders.OriginKey]);
        Assert.Equal("Debit", debitEvent.IntegrationHeaders[MovementHeaders.DirectionsKey]);

        // An event carrying no Originated movement declares no extension headers (base-default behaviour).
        var observedEvent = new MovementBearingTestEvent([Observed(SettlementDirection.Credit)]);
        Assert.Null(observedEvent.IntegrationHeaders);
    }

    /// <summary>
    /// A minimal, family-agnostic stand-in for a real Movement-bearing event (e.g. LoanDisbursed): it carries
    /// the Movement carrier and routes its <see cref="DomainEvent.IntegrationHeaders"/> through the generic
    /// <see cref="MovementHeaders"/> seam — exactly the shape every Movement-bearing family event adopts.
    /// </summary>
    private sealed record MovementBearingTestEvent(IReadOnlyList<Movement> Movements) : DomainEvent
    {
        public override IReadOnlyDictionary<string, string>? IntegrationHeaders =>
            MovementHeaders.ForOriginatedMovements(Movements);
    }
}
