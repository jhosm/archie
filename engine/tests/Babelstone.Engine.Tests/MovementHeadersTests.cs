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

    // ---- The ce_settlementtarget counterparty header (ADR-PC-043 slot 1) ------------------------------

    [Fact]
    public void An_engine_ca_target_promotes_a_settlementtarget_header_alongside_origin_and_directions()
    {
        // The counterparty-aware overload (ADR-PC-043 slot 1): a leg settling against the engine-owned CA
        // promotes ce_settlementtarget = engine-ca so the substrate's router diverts it WITHOUT reading the
        // payload. The ADR-PC-043 §D5 amendment (2026-07-11) ALSO promotes the per-movement destination
        // account_ref + amount as the settlement-command-body fields the substrate forwards untouched — still no
        // PII (opaque ref + integer cents).
        var headers = MovementHeaders.ForOriginatedMovements(
            [Originated(SettlementDirection.Credit)], SettlementTarget.EngineCa);

        Assert.NotNull(headers);
        Assert.Equal("Originated", headers![MovementHeaders.OriginKey]);
        Assert.Equal("Credit", headers[MovementHeaders.DirectionsKey]);
        Assert.Equal(MovementHeaders.EngineCaValue, headers[MovementHeaders.SettlementTargetKey]);
        Assert.Equal("engine-ca", headers[MovementHeaders.SettlementTargetKey]);
        // ADR-PC-043 §D5: the promoted destination + amount (SETTLEMENT_LEG_ACCOUNT_REF_PROMOTED, CA-17).
        Assert.Equal("acct-ref-opaque", headers[MovementHeaders.AccountRefsKey]);
        Assert.Equal("10000", headers[MovementHeaders.AmountsKey]);
        // origin + directions + settlementtarget + accountrefs + amounts — five entries on an engine-CA leg.
        Assert.Equal(5, headers.Count);
    }

    [Fact]
    public void An_engine_ca_multi_movement_event_promotes_parallel_ordered_accountref_and_amount_lists()
    {
        // A renewal records a rollover-debit AND an interest-credit on one append (ADR-PC-032 option b). The
        // engine-CA overload promotes movementaccountrefs / movementamounts as ORDERED lists parallel to
        // movementdirections — carrier order, one entry per Originated movement — so the substrate's fan-out can
        // reduce each to its own leg's destination + amount (SETTLEMENT_LEG_ACCOUNT_REF_PROMOTED for every leg).
        var rollover = Originated(SettlementDirection.Debit) with
        {
            AccountRef = "acct-source", Amount = new Money(5_000),
        };
        var interest = Originated(SettlementDirection.Credit) with
        {
            AccountRef = "acct-payout", Amount = new Money(319),
        };

        var headers = MovementHeaders.ForOriginatedMovements([rollover, interest], SettlementTarget.EngineCa);

        Assert.NotNull(headers);
        Assert.Equal("Debit,Credit", headers![MovementHeaders.DirectionsKey]);
        Assert.Equal("acct-source,acct-payout", headers[MovementHeaders.AccountRefsKey]);
        Assert.Equal("5000,319", headers[MovementHeaders.AmountsKey]);
    }

    [Fact]
    public void A_legacy_dda_target_promotes_no_settlementtarget_header_so_legacy_routing_is_unchanged()
    {
        // The default counterparty promotes NO target header — the router falls back to legacy, so a legacy
        // leg's header shape is byte-identical to the no-target overload (legacy routing UNCHANGED).
        var legacyTargeted = MovementHeaders.ForOriginatedMovements(
            [Originated(SettlementDirection.Credit)], SettlementTarget.LegacyDda);
        var noTarget = MovementHeaders.ForOriginatedMovements([Originated(SettlementDirection.Credit)]);

        Assert.NotNull(legacyTargeted);
        Assert.False(legacyTargeted!.ContainsKey(MovementHeaders.SettlementTargetKey));
        Assert.Equal(2, legacyTargeted.Count);
        // Byte-identical maps: same keys, same values.
        Assert.Equal(noTarget, legacyTargeted);
    }

    [Fact]
    public void An_observed_only_event_promotes_no_settlementtarget_header_even_when_engine_ca_targeted()
    {
        // No Originated cash leg → no headers at all, target or otherwise (the overload short-circuits on the
        // no-target result). An Observed movement has no cash leg to route.
        Assert.Null(MovementHeaders.ForOriginatedMovements(
            [Observed(SettlementDirection.Credit)], SettlementTarget.EngineCa));
    }

    [Fact]
    public void The_engine_ca_and_legacy_dda_wire_values_are_the_router_pinned_literals()
    {
        // The PRODUCER↔CONSUMER contract (mirrors MovementHeaderAutoStartContractTests): the wire tokens the
        // engine promotes are exactly the literals the substrate router matches on. The substrate does not
        // reference the engine, so the WIRE VALUE is the agreement — pin it here on the producer side.
        Assert.Equal("engine-ca", MovementHeaders.EngineCaValue);
        Assert.Equal("legacy-dda", MovementHeaders.LegacyDdaValue);
        Assert.Equal("settlementtarget", MovementHeaders.SettlementTargetKey);
        // ADR-PC-043 §D5: the engine-CA destination + amount header keys — the substrate pins the SAME wire
        // literals in Babelstone.Orchestrator.Tests.MovementHeaderAutoStartContractTests (the producer↔consumer
        // contract; the substrate cannot reference this constant, ADR-PC-019 §P2).
        Assert.Equal("movementaccountrefs", MovementHeaders.AccountRefsKey);
        Assert.Equal("movementamounts", MovementHeaders.AmountsKey);
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
