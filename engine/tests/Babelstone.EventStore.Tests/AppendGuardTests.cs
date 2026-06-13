using Babelstone.EventStore;
using Xunit;

namespace Babelstone.EventStore.Tests;

/// <summary>
/// Argument-guard behaviour of <see cref="PostgresEventStore.AppendAsync"/>. These
/// run in the default (Docker-free) lane: every guard throws before a connection is
/// opened, so no PostgreSQL is needed.
/// </summary>
public sealed class AppendGuardTests
{
    private static readonly PostgresEventStore Store = new("Host=unused;Database=unused");

    [Fact]
    public async Task Rejects_an_empty_event_list()
    {
        var streamId = Guid.NewGuid();
        var (_, outbox) = TestData.Pair(streamId, 0);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            Store.AppendAsync(streamId, -1, [], [outbox]));
    }

    [Fact]
    public async Task Allows_an_append_with_no_outbox_rows_past_the_guard()
    {
        // ADR-IC-017 §P1 (catalog-gated relay): a batch of only UNCATALOGUED events is store-only —
        // event rows but ZERO outbox rows. That is no longer an argument-guard rejection; the OLD
        // "no event without its outbox row" lower bound relaxes from 1 to 0 (atomicity — one
        // transaction — is unchanged; an outbox row, when present, still rides the same commit). With
        // no outbox rows the append now PASSES the count + contiguity guards and proceeds to open a
        // connection; against the unused connection string that surfaces as a connection-time failure
        // (NpgsqlException / SocketException), NOT the ArgumentOutOfRangeException the old guard threw.
        // Asserting "not the arg guard" keeps this in the Docker-free lane without a live DB.
        var streamId = Guid.NewGuid();
        var (e0, _) = TestData.Pair(streamId, 0);
        var ex = await Record.ExceptionAsync(() => Store.AppendAsync(streamId, -1, [e0], []));
        Assert.NotNull(ex);
        Assert.IsNotType<ArgumentOutOfRangeException>(ex);
    }

    [Fact]
    public async Task Rejects_more_outbox_rows_than_events()
    {
        // The upper bound still holds (ADR-IC-017 §P1): every outbox row corresponds to one appended
        // catalogued event, so an append may never carry MORE outbox rows than events.
        var streamId = Guid.NewGuid();
        var (e0, o0) = TestData.Pair(streamId, 0);
        var (_, oExtra) = TestData.Pair(streamId, 0);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            Store.AppendAsync(streamId, -1, [e0], [o0, oExtra]));
    }

    [Fact]
    public async Task Rejects_sequence_numbers_that_are_not_contiguous_from_expected_version()
    {
        var streamId = Guid.NewGuid();
        // expectedVersion -1 means the first event must be sequence 0; supply 5 instead.
        var (eGap, oGap) = TestData.Pair(streamId, 5);
        await Assert.ThrowsAsync<ArgumentException>(() =>
            Store.AppendAsync(streamId, -1, [eGap], [oGap]));
    }

    [Fact]
    public async Task Rejects_an_event_whose_stream_id_does_not_match()
    {
        var streamId = Guid.NewGuid();
        var (eOther, oOther) = TestData.Pair(Guid.NewGuid(), 0); // different stream
        await Assert.ThrowsAsync<ArgumentException>(() =>
            Store.AppendAsync(streamId, -1, [eOther], [oOther]));
    }
}
