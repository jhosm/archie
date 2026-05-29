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
    public async Task Rejects_an_append_with_no_outbox_rows()
    {
        var streamId = Guid.NewGuid();
        var (e0, _) = TestData.Pair(streamId, 0);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            Store.AppendAsync(streamId, -1, [e0], []));
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
