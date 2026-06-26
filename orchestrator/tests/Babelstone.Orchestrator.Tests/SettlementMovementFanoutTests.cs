using Babelstone.Orchestrator.Inbox;
using Babelstone.Orchestrator.Saga.Settlement;
using Xunit;

namespace Babelstone.Orchestrator.Tests;

/// <summary>
/// Unit proof of the multi-direction Movement fan-out (ADR-PC-032 §A9/§A10, option b; bd babelstone-t7o3.21).
/// In plain English: a single event can carry money moving two opposite ways at once — a deposit renewal rolls
/// the principal over (a debit) AND pays the interest (a credit). One settlement saga instance branches to ONE
/// direction, so the substrate turns that one event into one settlement subject per Movement, each gated by its
/// own direction. These pin that the fan-out projector reads the producer's ordered <c>movementdirections</c>
/// list, derives a distinct, DETERMINISTIC subject per Movement (so a redelivery re-derives the same subjects —
/// effectively-once per leg), and reduces each leg's list to its own single direction.
/// </summary>
public sealed class SettlementMovementFanoutTests
{
    private const string OriginHeader = "movementorigin";
    private const string DirectionsHeader = "movementdirections";

    private static SagaInboxEvent MultiDirectionEvent(string directions, Guid? subject = null) => new(
        MessageId: Guid.Parse("11111111-1111-1111-1111-111111111111"),
        ProcessId: subject ?? Guid.Parse("22222222-2222-2222-2222-222222222222"),
        EventType: "DepositRenewed",
        SourceTopic: "term_deposit",
        CorrelationId: Guid.Parse("33333333-3333-3333-3333-333333333333"),
        TraceParent: "00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01",
        ExtensionHeaders: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [OriginHeader] = "Originated",
            [DirectionsHeader] = directions,  // the ordered list the fan-out reads
            ["scaacr"] = "urn:bank:sca:psd2",
            ["scaauthtime"] = "1750000000",
        });

    [Fact]
    public void A_multi_direction_event_fans_into_one_instance_per_movement_in_carrier_order()
    {
        var projections = SettlementSagaModule.FanOutByMovementDirection(MultiDirectionEvent("Debit,Credit"));

        Assert.Equal(2, projections.Count);
        // Carrier order is preserved: the debit leg first, the credit leg second (feature-design §6). Each
        // leg's movementdirections list is reduced to its OWN single direction.
        Assert.Equal("Debit", projections[0].ExtensionHeaders![DirectionsHeader]);
        Assert.Equal("Credit", projections[1].ExtensionHeaders![DirectionsHeader]);
    }

    [Fact]
    public void The_primary_leg_keeps_the_events_own_ids_the_secondary_is_derived_and_distinct()
    {
        var source = MultiDirectionEvent("Debit,Credit");
        var projections = SettlementSagaModule.FanOutByMovementDirection(source);

        // Index 0 (primary) keeps the event's own subject + dedup id — the established single-instance path.
        Assert.Equal(source.ProcessId, projections[0].ProcessId);
        Assert.Equal(source.MessageId, projections[0].MessageId);

        // Index 1 (secondary) is a distinct derived subject + dedup id — its OWN saga instance + its OWN
        // dispatcher FIFO lane, so the two legs settle independently with no collision.
        Assert.NotEqual(source.ProcessId, projections[1].ProcessId);
        Assert.NotEqual(source.MessageId, projections[1].MessageId);
        Assert.NotEqual(projections[0].ProcessId, projections[1].ProcessId);
    }

    [Fact]
    public void The_derivation_is_deterministic_so_a_redelivery_re_derives_the_same_subjects()
    {
        // Effectively-once per leg: the same event re-projects to byte-identical subjects + dedup ids, so the
        // auto-start PK and the inbox dedup absorb a redelivery (no double-start, no double-settle).
        var first = SettlementSagaModule.FanOutByMovementDirection(MultiDirectionEvent("Debit,Credit"));
        var second = SettlementSagaModule.FanOutByMovementDirection(MultiDirectionEvent("Debit,Credit"));

        for (var i = 0; i < first.Count; i++)
        {
            Assert.Equal(first[i].ProcessId, second[i].ProcessId);
            Assert.Equal(first[i].MessageId, second[i].MessageId);
        }
    }

    [Fact]
    public void Each_leg_carries_a_single_entry_list_so_it_never_re_fans_out()
    {
        var projections = SettlementSagaModule.FanOutByMovementDirection(MultiDirectionEvent("Debit,Credit"));

        foreach (var leg in projections)
        {
            // The list is REDUCED to this leg's one direction by the fan-out — a single-entry list the
            // established substitutor resolves, and which is inert on re-entry (no recursion past depth 1).
            Assert.DoesNotContain(",", leg.ExtensionHeaders![DirectionsHeader]);
            // Re-projecting a leg yields itself (length 1) — proving it does not fan out again.
            Assert.Single(SettlementSagaModule.FanOutByMovementDirection(leg));
        }
    }

    [Fact]
    public void Each_leg_carries_the_attested_sca_claims_forward()
    {
        // The SCA claims (bd babelstone-t7o3.19) ride EVERY leg — the substrate names neither them nor any
        // family, it copies whatever extension attributes the event carried so each leg's cash dispatch
        // re-checks freshness against the SAME attested proof.
        var projections = SettlementSagaModule.FanOutByMovementDirection(MultiDirectionEvent("Debit,Credit"));

        foreach (var leg in projections)
        {
            Assert.Equal("urn:bank:sca:psd2", leg.ExtensionHeaders!["scaacr"]);
            Assert.Equal("1750000000", leg.ExtensionHeaders!["scaauthtime"]);
        }
    }

    [Fact]
    public void A_single_direction_event_does_not_fan_out()
    {
        // A one-entry movementdirections list → the lone event is returned unchanged; the substrate starts
        // exactly one instance (the established path for the 7 standalone legs — disbursement, maturity,
        // coupon, early-termination).
        var single = new SagaInboxEvent(
            MessageId: Guid.NewGuid(),
            ProcessId: Guid.NewGuid(),
            EventType: "DepositMatured",
            SourceTopic: "term_deposit",
            CorrelationId: null,
            ExtensionHeaders: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [OriginHeader] = "Originated",
                [DirectionsHeader] = "Credit",
            });

        var projections = SettlementSagaModule.FanOutByMovementDirection(single);
        Assert.Single(projections);
        Assert.Same(single, projections[0]);
    }
}
