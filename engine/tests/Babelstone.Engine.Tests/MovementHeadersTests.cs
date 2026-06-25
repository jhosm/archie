using Babelstone.FinancialTypes;
using Xunit;

namespace Babelstone.Engine.Tests;

/// <summary>
/// Tests for the GENERIC engine-spine producer seam <see cref="MovementHeaders"/> (ADR-PC-032 §A7/§A8;
/// ADR-IC-018 §P5/§D5; bd babelstone-t7o3.20). In plain English: when a family event records that money was
/// decided, this seam turns the event's <see cref="Movement"/> into the two closed-enum CloudEvents header
/// VALUES the substrate-owned settlement saga auto-starts on — without naming any family. These prove the
/// PRODUCER half of the hop the settlement saga (the consumer) was already built to read: the
/// <c>movementorigin</c> = <c>Originated</c> auto-start key and the <c>movementdirection</c> = <c>Debit</c> |
/// <c>Credit</c> branch key, the exact strings the substrate's <c>SettlementSagaModule</c> /
/// <c>SettlementProcess</c> match on. The relay-side promotion of these values to <c>ce_*</c> headers is
/// covered alongside the existing extension-attribute promotion in <c>WireFormatTests</c>.
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
    public void A_single_originated_movement_promotes_origin_and_direction_as_closed_enum_strings(
        SettlementDirection direction, string expectedDirection)
    {
        // The two routing discriminators the substrate reads off the headers (never the payload): origin
        // tells the auto-start predicate there IS a cash leg to drive (Originated), direction tells the
        // substitutor which branch. The values are the enum member NAMES — the SAME strings the substrate's
        // SettlementSagaModule.OriginatedValue / SettlementProcess direction values match on.
        var headers = MovementHeaders.ForOriginatedMovements([Originated(direction)]);

        Assert.NotNull(headers);
        Assert.Equal("Originated", headers![MovementHeaders.OriginKey]);
        Assert.Equal(expectedDirection, headers[MovementHeaders.DirectionKey]);
        // No amount, no account_ref, no command_id — only the two closed-enum discriminators ride the
        // headers (ADR-PC-004 §P2 / ADR-PC-032 §A8). Those stay in the payload.
        Assert.Equal(2, headers.Count);
    }

    [Fact]
    public void Several_originated_movements_that_agree_on_direction_promote_one_direction_header()
    {
        // Multiple Originated movements that all move money the SAME way (e.g. two debits) carry one
        // movementdirection — the agreed direction. (A renewal-style debit+credit pair is the disagreeing
        // case, tested below.)
        var headers = MovementHeaders.ForOriginatedMovements(
            [Originated(SettlementDirection.Debit), Originated(SettlementDirection.Debit)]);

        Assert.NotNull(headers);
        Assert.Equal("Originated", headers![MovementHeaders.OriginKey]);
        Assert.Equal("Debit", headers[MovementHeaders.DirectionKey]);
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
    public void An_originated_movement_alongside_an_observed_one_promotes_the_originated_direction()
    {
        // Only the Originated movement has a cash leg; the Observed one is ignored for header purposes.
        var headers = MovementHeaders.ForOriginatedMovements(
            [Observed(SettlementDirection.Debit), Originated(SettlementDirection.Credit)]);

        Assert.NotNull(headers);
        Assert.Equal("Credit", headers![MovementHeaders.DirectionKey]);
    }

    [Fact]
    public void An_event_with_originated_movements_in_both_directions_fails_loud_the_multi_direction_split()
    {
        // The v1 multi-Movement split (ADR-PC-032 §A8): a single movementdirection header cannot express both
        // a debit and a credit, and the substrate's substitutor reads exactly one. Fail loud rather than
        // promote a guessed branch — the genuine multi-direction event is a substrate-side follow-up.
        var ex = Assert.Throws<InvalidOperationException>(() =>
            MovementHeaders.ForOriginatedMovements(
                [Originated(SettlementDirection.Debit), Originated(SettlementDirection.Credit)]));

        Assert.Contains("both directions", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_movement_bearing_event_routes_its_IntegrationHeaders_through_the_generic_helper()
    {
        // The end-to-end PRODUCER shape: a Movement-bearing event's IntegrationHeaders override returns the
        // helper's map, so the engine relay promotes ce_movementorigin / ce_movementdirection for free,
        // family-agnostically (no family event type is named in the spine).
        var debitEvent = new MovementBearingTestEvent([Originated(SettlementDirection.Debit)]);
        Assert.NotNull(debitEvent.IntegrationHeaders);
        Assert.Equal("Originated", debitEvent.IntegrationHeaders![MovementHeaders.OriginKey]);
        Assert.Equal("Debit", debitEvent.IntegrationHeaders[MovementHeaders.DirectionKey]);

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
