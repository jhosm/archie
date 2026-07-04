using Babelstone.Orchestrator.Inbox;
using Babelstone.Orchestrator.Saga.Settlement;
using Xunit;

namespace Babelstone.Orchestrator.Tests;

/// <summary>
/// Unit proof of the per-occurrence Movement fan-out (ADR-PC-032 §A9/§A10, option b + the
/// per-occurrence-identity revision 2026-07-04; bd babelstone-t7o3.21 / babelstone-3o6m). In plain English:
/// every time money moves, the settlement machinery needs its own saga instance — including the SECOND time
/// money moves on the SAME account (a loan's monthly installments) and including one event moving money two
/// opposite ways at once (a renewal's rollover-debit + interest-credit). These pin that the fan-out projector
/// reads the producer's ordered <c>movementdirections</c> list, derives a distinct, DETERMINISTIC
/// per-occurrence process id per Movement from (subject, event id, index) — so a redelivery re-derives the
/// same ids (effectively-once per leg) while a LATER event on the same subject derives fresh ones — reduces
/// each leg's list to its own single direction, and preserves the account/instrument linkage on
/// <see cref="SagaInboxEvent.SubjectId"/> (the <c>saga_state.subject_id</c> column the LCD-2 probe keys on).
/// </summary>
public sealed class SettlementMovementFanoutTests
{
    private const string OriginHeader = "movementorigin";
    private const string DirectionsHeader = "movementdirections";

    private static readonly Guid Subject = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid EventId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private static SagaInboxEvent MovementEvent(
        string directions, Guid? subject = null, Guid? messageId = null) => new(
        MessageId: messageId ?? EventId,
        ProcessId: subject ?? Subject,
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
        var projections = SettlementSagaModule.FanOutByMovementDirection(MovementEvent("Debit,Credit"));

        Assert.Equal(2, projections.Count);
        // Carrier order is preserved: the debit leg first, the credit leg second (feature-design §6). Each
        // leg's movementdirections list is reduced to its OWN single direction.
        Assert.Equal("Debit", projections[0].ExtensionHeaders![DirectionsHeader]);
        Assert.Equal("Credit", projections[1].ExtensionHeaders![DirectionsHeader]);
    }

    [Fact]
    public void Every_leg_gets_a_derived_per_occurrence_process_id_with_the_subject_preserved()
    {
        var source = MovementEvent("Debit,Credit");
        var projections = SettlementSagaModule.FanOutByMovementDirection(source);

        // NO leg keeps the bare ce_subject as its process id any more (ADR-PC-032 §A9/§A10 Revised
        // 2026-07-04): each is a distinct per-occurrence derivation of (subject, event id, index) — its OWN
        // saga instance + its OWN dispatcher FIFO lane — and each carries the real subject on SubjectId (the
        // saga_state.subject_id linkage the LCD-2 probe scans).
        Assert.NotEqual(source.ProcessId, projections[0].ProcessId);
        Assert.NotEqual(source.ProcessId, projections[1].ProcessId);
        Assert.NotEqual(projections[0].ProcessId, projections[1].ProcessId);
        Assert.All(projections, leg => Assert.Equal(source.ProcessId, leg.SubjectId));

        // The PRIMARY leg keeps the event's own dedup id (one physical delivery ↔ one primary advance); the
        // secondary's is derived and distinct.
        Assert.Equal(source.MessageId, projections[0].MessageId);
        Assert.NotEqual(source.MessageId, projections[1].MessageId);
    }

    [Fact]
    public void The_derivation_is_deterministic_so_a_redelivery_re_derives_the_same_ids()
    {
        // Effectively-once per leg: the same event re-projects to byte-identical process ids + dedup ids, so
        // the auto-start PK and the inbox dedup absorb a redelivery (no double-start, no double-settle).
        var first = SettlementSagaModule.FanOutByMovementDirection(MovementEvent("Debit,Credit"));
        var second = SettlementSagaModule.FanOutByMovementDirection(MovementEvent("Debit,Credit"));

        for (var i = 0; i < first.Count; i++)
        {
            Assert.Equal(first[i].ProcessId, second[i].ProcessId);
            Assert.Equal(first[i].MessageId, second[i].MessageId);
        }
    }

    [Fact]
    public void A_later_occurrence_on_the_same_subject_derives_a_fresh_instance_and_fresh_acl_tokens()
    {
        // THE per-occurrence point (bd babelstone-3o6m / Q-BH): installment 1 and installment 2 are two
        // EVENTS on the SAME subject (each with its own ce_id). Their settlement instances must be distinct
        // sagas — so occurrence 2 never no-ops at occurrence 1's terminal SETTLEMENT_COMPLETED row...
        var installment1 = Assert.Single(SettlementSagaModule.FanOutByMovementDirection(
            MovementEvent("Debit", messageId: Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001"))));
        var installment2 = Assert.Single(SettlementSagaModule.FanOutByMovementDirection(
            MovementEvent("Debit", messageId: Guid.Parse("aaaaaaaa-0000-0000-0000-000000000002"))));

        Assert.NotEqual(installment1.ProcessId, installment2.ProcessId);
        Assert.Equal(installment1.SubjectId, installment2.SubjectId);

        // ...and — because the ACL idempotency references derive from the process id (ADR-IC-012 §P4;
        // SettlementReferences) — installment 2's debit tokens can NOT dedup against installment 1's: the
        // per-occurrence process id yields per-occurrence external_references. This is the design's point.
        Assert.NotEqual(
            SettlementReferences.Derive(SettlementReferences.CoreHoldPrefix, installment1.ProcessId),
            SettlementReferences.Derive(SettlementReferences.CoreHoldPrefix, installment2.ProcessId));
        Assert.NotEqual(
            SettlementReferences.Derive(SettlementReferences.ReservationPrefix, installment1.ProcessId),
            SettlementReferences.Derive(SettlementReferences.ReservationPrefix, installment2.ProcessId));

        // While a RE-DERIVATION for the same occurrence stays stable — the retry/reissue token the ACL
        // dedups on is unchanged by this revision.
        Assert.Equal(
            SettlementReferences.Derive(SettlementReferences.CoreHoldPrefix, installment1.ProcessId),
            SettlementReferences.Derive(
                SettlementReferences.CoreHoldPrefix,
                SettlementMovementFanout.OccurrenceProcessId(
                    Subject, Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001"), 0)));
    }

    [Fact]
    public void A_projected_leg_is_inert_on_re_entry()
    {
        var projections = SettlementSagaModule.FanOutByMovementDirection(MovementEvent("Debit,Credit"));

        foreach (var leg in projections)
        {
            // The list is REDUCED to this leg's one direction by the fan-out — a single-entry list the
            // established substitutor resolves...
            Assert.DoesNotContain(",", leg.ExtensionHeaders![DirectionsHeader]);
            // ...and the non-null SubjectId stamp makes the projection inert on re-entry: re-projecting a
            // leg yields ITSELF (no re-derivation from an already-derived id — no recursion past depth 1).
            Assert.Same(leg, Assert.Single(SettlementSagaModule.FanOutByMovementDirection(leg)));
        }
    }

    [Fact]
    public void Each_leg_carries_the_attested_sca_claims_forward()
    {
        // The SCA claims (bd babelstone-t7o3.19) ride EVERY leg — the substrate names neither them nor any
        // family, it copies whatever extension attributes the event carried so each leg's cash dispatch
        // re-checks freshness against the SAME attested proof.
        var projections = SettlementSagaModule.FanOutByMovementDirection(MovementEvent("Debit,Credit"));

        foreach (var leg in projections)
        {
            Assert.Equal("urn:bank:sca:psd2", leg.ExtensionHeaders!["scaacr"]);
            Assert.Equal("1750000000", leg.ExtensionHeaders!["scaauthtime"]);
        }
    }

    [Fact]
    public void A_single_direction_event_projects_to_exactly_one_per_occurrence_instance()
    {
        // A one-entry movementdirections list → exactly ONE instance (the 7 standalone legs — disbursement,
        // maturity, coupon, early-termination, installment collection...), at its per-occurrence derived id,
        // keeping the event's own dedup id and carrying the subject linkage.
        var single = MovementEvent("Credit");

        var projections = SettlementSagaModule.FanOutByMovementDirection(single);
        var leg = Assert.Single(projections);
        Assert.Equal(
            SettlementMovementFanout.OccurrenceProcessId(single.ProcessId, single.MessageId, 0),
            leg.ProcessId);
        Assert.Equal(single.MessageId, leg.MessageId);
        Assert.Equal(single.ProcessId, leg.SubjectId);
        Assert.Equal("Credit", leg.ExtensionHeaders![DirectionsHeader]);
    }

    [Fact]
    public void An_event_declaring_no_directions_flows_through_unchanged()
    {
        // Defensive depth: the producer always emits movementdirections for an Originated event (ADR-PC-032
        // §A9), but a directions-less event must NOT mint an occurrence id from a phantom movement — it keeps
        // the legacy single instance on its own ce_subject.
        var bare = new SagaInboxEvent(
            MessageId: Guid.NewGuid(),
            ProcessId: Guid.NewGuid(),
            EventType: "DepositMatured",
            SourceTopic: "term_deposit",
            CorrelationId: null,
            ExtensionHeaders: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [OriginHeader] = "Originated",
            });

        Assert.Same(bare, Assert.Single(SettlementSagaModule.FanOutByMovementDirection(bare)));
    }
}
