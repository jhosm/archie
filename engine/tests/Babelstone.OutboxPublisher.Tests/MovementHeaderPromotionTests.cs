using System.Text;
using Babelstone.Engine;
using Babelstone.EventStore;
using Babelstone.Families.TermDeposit;
using Babelstone.FinancialTypes;
using Xunit;

namespace Babelstone.OutboxPublisher.Tests;

/// <summary>
/// Relay-side proof that a Movement-bearing event's promoted <c>movementorigin</c> / <c>movementdirections</c>
/// extension attributes ride out as <c>ce_movementorigin</c> / <c>ce_movementdirections</c> CloudEvents
/// headers (ADR-PC-032 §A7/§A8; ADR-IC-018 §P5; bd babelstone-t7o3.20). In plain English: the engine spine
/// derives the two routing strings from the event's <see cref="Movement"/>(s) (via
/// <see cref="MovementHeaders"/>); they are persisted on the outbox row's <c>integration_headers</c> column;
/// and the relay's pure header build (<see cref="OutboxDrainer.BuildHeadersCore"/>) copies them to
/// <c>ce_*</c> headers — exactly the headers the substrate-owned settlement saga reads to auto-start and to
/// fan out / pick the debit/credit branch. The relay names no key; it copies whatever the event declared, so
/// the seam stays family-agnostic.
/// </summary>
public sealed class MovementHeaderPromotionTests
{
    private static Movement Originated(SettlementDirection direction) => new(
        AccountRef: "acct-ref-opaque",
        Direction: direction,
        Amount: new Money(50_000),
        ValueDate: new DateOnly(2026, 6, 25),
        Operation: MovementOperation.Disburse,
        Origin: MovementOrigin.Originated,
        CommandId: Guid.NewGuid());

    private static OutboxRow Row(IReadOnlyDictionary<string, string>? integrationHeaders) => new(
        EventId: Guid.NewGuid(),
        AggregateType: "personal_loan",
        AggregateId: Guid.NewGuid(),
        SequenceNumber: 1,
        EventType: "personal_loan.LoanDisbursed",
        Payload: ReadOnlyMemory<byte>.Empty,
        SchemaId: 1,
        Status: OutboxStatus.Pending,
        CreatedAt: DateTimeOffset.UnixEpoch,
        PublishedAt: null,
        IntegrationHeaders: integrationHeaders);

    private static string? HeaderValue(Confluent.Kafka.Headers headers, string key)
        => headers.TryGetLastBytes(key, out var bytes) ? Encoding.UTF8.GetString(bytes) : null;

    [Theory]
    [InlineData(SettlementDirection.Debit, "Debit")]
    [InlineData(SettlementDirection.Credit, "Credit")]
    public void A_movement_bearing_row_promotes_ce_movementorigin_and_ce_movementdirections(
        SettlementDirection direction, string expectedDirection)
    {
        // The engine-spine seam derives the header map from the event's Originated Movement; the row carries
        // it on integration_headers; the relay promotes each to a ce_<key> header. A standalone leg's
        // movementdirections list is one entry.
        var declared = MovementHeaders.ForOriginatedMovements([Originated(direction)]);
        var headers = OutboxDrainer.BuildHeadersCore(Row(declared), source: "urn:babelstone:engine");

        Assert.Equal("Originated", HeaderValue(headers, "ce_movementorigin"));
        Assert.Equal(expectedDirection, HeaderValue(headers, "ce_movementdirections"));
        // The standard CE set is still present and unaffected — the ce_type is the REAL event record name
        // (LoanDisbursed), unchanged; movement routing rides the extension headers, never ce_type.
        Assert.Equal("1.0", HeaderValue(headers, "ce_specversion"));
        Assert.Equal("com.bank.deposits.LoanDisbursed", HeaderValue(headers, "ce_type"));
    }

    [Fact]
    public void A_multi_direction_row_promotes_the_ordered_ce_movementdirections_list()
    {
        // A renewal's rollover-debit + interest-credit ride ONE event; the seam emits the ordered
        // movementdirections list and the relay promotes it to a ce_* header — the list is what the substrate
        // fans out on (ADR-PC-032 §A9/§A10).
        var declared = MovementHeaders.ForOriginatedMovements(
            [Originated(SettlementDirection.Debit), Originated(SettlementDirection.Credit)]);
        var headers = OutboxDrainer.BuildHeadersCore(Row(declared), source: "urn:babelstone:engine");

        Assert.Equal("Originated", HeaderValue(headers, "ce_movementorigin"));
        Assert.Equal("Debit,Credit", HeaderValue(headers, "ce_movementdirections"));
    }

    [Fact]
    public void An_event_with_no_originated_movement_promotes_no_movement_headers()
    {
        // ForOriginatedMovements returns null for an Observed-only / movement-free event, so the row's
        // integration_headers is null and the relay emits only the standard CE set.
        var declared = MovementHeaders.ForOriginatedMovements(
            [Originated(SettlementDirection.Credit) with { Origin = MovementOrigin.Observed }]);
        Assert.Null(declared);

        var headers = OutboxDrainer.BuildHeadersCore(Row(declared), source: "urn:babelstone:engine");
        Assert.Null(HeaderValue(headers, "ce_movementorigin"));
        Assert.Null(HeaderValue(headers, "ce_movementdirections"));
    }

    // ---- The term-deposit producer promotes ce_settlementtarget end-to-end (ADR-PC-043 slot 1) ----------

    [Fact]
    public void An_engine_ca_targeted_DepositMatured_promotes_ce_settlementtarget_through_the_relay()
    {
        // The term-deposit PRODUCER proof for bd babelstone-u79p.2: a DepositMatured whose payout settles
        // against the engine-owned CA carries SettlementTarget.EngineCa (the family stamps it, Step B), so its
        // IntegrationHeaders declares ce_settlementtarget = engine-ca alongside the movement headers, and the
        // relay promotes it as a real ce_* header. The routing token rides the header ALONE — the substrate
        // never reads Movement.AccountRef from the body (ADR-IC-018 §D5). The persistent payout account stays
        // on Movement.AccountRef (Step A), NOT on the header.
        var matured = new DepositMatured(
            PrincipalReturned: new Money(1_000_000),
            NetInterestPaid: new Money(21_900),
            TotalPayout: new Money(1_021_900),
            MaturedOn: new DateOnly(2026, 12, 31),
            Movements:
            [
                new Movement(
                    AccountRef: "acct-payout-opaque",
                    Direction: SettlementDirection.Credit,
                    Amount: new Money(1_021_900),
                    ValueDate: new DateOnly(2026, 12, 31),
                    Operation: MovementOperation.PayMaturity,
                    Origin: MovementOrigin.Originated,
                    CommandId: Guid.NewGuid()),
            ])
        {
            SettlementTarget = SettlementTarget.EngineCa,
        };

        var headers = OutboxDrainer.BuildHeadersCore(
            Row(matured.IntegrationHeaders), source: "urn:babelstone:engine");

        Assert.Equal("Originated", HeaderValue(headers, "ce_movementorigin"));
        Assert.Equal("Credit", HeaderValue(headers, "ce_movementdirections"));
        Assert.Equal("engine-ca", HeaderValue(headers, "ce_settlementtarget"));
    }

    [Fact]
    public void A_default_legacy_DepositMatured_promotes_no_ce_settlementtarget_so_legacy_routing_is_unchanged()
    {
        // The DEFAULT counterparty (LegacyDda) promotes NO target header, so a legacy-routed maturity is
        // byte-identical to the pre-u79p.2 no-target shape: only ce_movementorigin / ce_movementdirections
        // ride, and the substrate router falls back to the legacy core (UNCHANGED). This is the guarantee
        // that an instance which has not opted into engine-CA settlement is untouched.
        var matured = new DepositMatured(
            PrincipalReturned: new Money(1_000_000),
            NetInterestPaid: new Money(21_900),
            TotalPayout: new Money(1_021_900),
            MaturedOn: new DateOnly(2026, 12, 31),
            Movements:
            [
                new Movement(
                    AccountRef: "acct-payout-opaque",
                    Direction: SettlementDirection.Credit,
                    Amount: new Money(1_021_900),
                    ValueDate: new DateOnly(2026, 12, 31),
                    Operation: MovementOperation.PayMaturity,
                    Origin: MovementOrigin.Originated,
                    CommandId: Guid.NewGuid()),
            ]);
        // No SettlementTarget set → the record default is LegacyDda.
        Assert.Equal(SettlementTarget.LegacyDda, matured.SettlementTarget);

        var headers = OutboxDrainer.BuildHeadersCore(
            Row(matured.IntegrationHeaders), source: "urn:babelstone:engine");

        Assert.Equal("Originated", HeaderValue(headers, "ce_movementorigin"));
        Assert.Equal("Credit", HeaderValue(headers, "ce_movementdirections"));
        Assert.Null(HeaderValue(headers, "ce_settlementtarget"));
    }
}
