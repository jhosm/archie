using System.Text;
using Babelstone.Engine;
using Babelstone.EventStore;
using Babelstone.FinancialTypes;
using Xunit;

namespace Babelstone.OutboxPublisher.Tests;

/// <summary>
/// Relay-side proof that a Movement-bearing event's promoted <c>movementorigin</c> / <c>movementdirection</c>
/// extension attributes ride out as <c>ce_movementorigin</c> / <c>ce_movementdirection</c> CloudEvents
/// headers (ADR-PC-032 §A7/§A8; ADR-IC-018 §P5; bd babelstone-t7o3.20). In plain English: the engine spine
/// derives the two routing strings from the event's <see cref="Movement"/> (via
/// <see cref="MovementHeaders"/>); they are persisted on the outbox row's <c>integration_headers</c> column;
/// and the relay's pure header build (<see cref="OutboxDrainer.BuildHeadersCore"/>) copies them to
/// <c>ce_*</c> headers — exactly the headers the substrate-owned settlement saga reads to auto-start and to
/// pick the debit/credit branch. The relay names no key; it copies whatever the event declared, so the seam
/// stays family-agnostic.
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
    public void A_movement_bearing_row_promotes_ce_movementorigin_and_ce_movementdirection(
        SettlementDirection direction, string expectedDirection)
    {
        // The engine-spine seam derives the header map from the event's Originated Movement; the row carries
        // it on integration_headers; the relay promotes each to a ce_<key> header.
        var declared = MovementHeaders.ForOriginatedMovements([Originated(direction)]);
        var headers = OutboxDrainer.BuildHeadersCore(Row(declared), source: "urn:babelstone:engine");

        Assert.Equal("Originated", HeaderValue(headers, "ce_movementorigin"));
        Assert.Equal(expectedDirection, HeaderValue(headers, "ce_movementdirection"));
        // The standard CE set is still present and unaffected — the ce_type is the REAL event record name
        // (LoanDisbursed), unchanged; movement routing rides the extension headers, never ce_type.
        Assert.Equal("1.0", HeaderValue(headers, "ce_specversion"));
        Assert.Equal("com.bank.deposits.LoanDisbursed", HeaderValue(headers, "ce_type"));
    }

    [Fact]
    public void A_multi_direction_row_promotes_the_ce_movementdirections_composite()
    {
        // A renewal's rollover-debit + interest-credit ride ONE event; the seam emits movementdirection (the
        // first direction) AND the movementdirections composite, and the relay promotes BOTH to ce_* headers
        // — the composite is what the substrate fans out on (ADR-PC-032 §A9 amendment 2026-06-26).
        var declared = MovementHeaders.ForOriginatedMovements(
            [Originated(SettlementDirection.Debit), Originated(SettlementDirection.Credit)]);
        var headers = OutboxDrainer.BuildHeadersCore(Row(declared), source: "urn:babelstone:engine");

        Assert.Equal("Originated", HeaderValue(headers, "ce_movementorigin"));
        Assert.Equal("Debit", HeaderValue(headers, "ce_movementdirection"));
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
        Assert.Null(HeaderValue(headers, "ce_movementdirection"));
    }
}
