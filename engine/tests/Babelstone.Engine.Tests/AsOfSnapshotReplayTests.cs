using Babelstone.Engine;
using FsCheck.Xunit;
using Xunit;

namespace Babelstone.Engine.Tests;

/// <summary>
/// A.13 as-of replay honours snapshots (bd babelstone-e6fr.13 / ADR-PC-003 §P3 + ADR-PC-002
/// bitemporal). A point-in-time read (<see cref="AggregateRuntime{TState}.LoadAsOfSequenceAsync"/>)
/// must seed from the latest VALID snapshot at or BELOW the as-of sequence and fold only the tail up to
/// the point — never a snapshot taken PAST the point (which is the future relative to the read). It must
/// produce state BYTE-FOR-BYTE identical to a cold fold-to-as-of (the snapshot is a pure optimisation),
/// and it must fall back to a cold fold from zero when no snapshot qualifies (asOf below the earliest
/// snapshot — the §P3 correctness fallback). Real PG18; Integration.
/// </summary>
[Trait("Category", "Integration")]
public sealed class AsOfSnapshotReplayTests(EngineFixture fixture) : IClassFixture<EngineFixture>
{
    private static readonly DateTimeOffset SnapshotTime = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private SnapshotStore<CounterState> Snapshots =>
        new(fixture.SnapshotStorage, new JsonStateSerializer<CounterState>());

    [Fact]
    public async Task As_of_read_uses_a_snapshot_at_or_below_the_point_and_equals_the_cold_fold()
    {
        // Build a 5-event stream (sequences 0..4) and place a snapshot at sequence 2. An as-of read at
        // sequence 3 must seed from the snapshot at 2 and fold only the tail [3] — and equal a cold
        // fold-to-3 byte-for-byte.
        var runtime = fixture.DurableRuntime(withSnapshots: true);
        var streamId = Guid.NewGuid();
        await runtime.AppendAsync(
            streamId, -1,
            [new Incremented(1), new Incremented(2), new Incremented(4), new Incremented(8), new Incremented(16)],
            fixture.Context());

        // Snapshot at sequence 2 (Total after 1+2+4 == 7).
        var atTwo = await runtime.LoadAsOfSequenceAsync(streamId, 2);
        await Snapshots.PutAsync(streamId, atTwo.Version, atTwo.LastEventId!.Value, atTwo.State, SnapshotTime);

        // As-of read at sequence 3 (1+2+4+8 == 15), with the snapshot at 2 available.
        var viaSnapshot = await runtime.LoadAsOfSequenceAsync(streamId, 3);

        // A cold fold-to-3 on a runtime with NO snapshots — the byte-for-byte reference.
        var coldFold = await fixture.DurableRuntime().LoadAsOfSequenceAsync(streamId, 3);

        Assert.Equal(15, viaSnapshot.State.Total);
        Assert.Equal(coldFold.State, viaSnapshot.State); // byte-identical (record value equality)
        Assert.Equal(coldFold.Version, viaSnapshot.Version);
        Assert.Equal(3, viaSnapshot.Version);
    }

    [Fact]
    public async Task As_of_read_below_the_earliest_snapshot_folds_cold_from_zero()
    {
        // The cold-path acceptance (§P3): a snapshot exists at sequence 3, but the as-of point is
        // sequence 1 — BELOW the earliest snapshot. The read must NOT seed from that future snapshot;
        // it folds cold from zero and still equals the cold fold-to-1.
        var runtime = fixture.DurableRuntime(withSnapshots: true);
        var streamId = Guid.NewGuid();
        await runtime.AppendAsync(
            streamId, -1,
            [new Incremented(1), new Incremented(2), new Incremented(4), new Incremented(8)],
            fixture.Context());

        // Snapshot at sequence 3 (the head) — PAST the as-of point we will read.
        var atThree = await runtime.LoadAsOfSequenceAsync(streamId, 3);
        await Snapshots.PutAsync(streamId, atThree.Version, atThree.LastEventId!.Value, atThree.State, SnapshotTime);

        // As-of read at sequence 1 (1+2 == 3): the snapshot at 3 is the future and must be skipped.
        var viaSnapshot = await runtime.LoadAsOfSequenceAsync(streamId, 1);
        var coldFold = await fixture.DurableRuntime().LoadAsOfSequenceAsync(streamId, 1);

        Assert.Equal(3, viaSnapshot.State.Total);
        Assert.Equal(coldFold.State, viaSnapshot.State);
        Assert.Equal(1, viaSnapshot.Version);
    }

    [Fact]
    public async Task As_of_read_exactly_at_the_snapshot_point_uses_the_snapshot_with_an_empty_tail()
    {
        // A snapshot at sequence 2 and an as-of read at exactly 2: seed from the snapshot, fold an EMPTY
        // tail, equal the cold fold-to-2.
        var runtime = fixture.DurableRuntime(withSnapshots: true);
        var streamId = Guid.NewGuid();
        await runtime.AppendAsync(
            streamId, -1, [new Incremented(3), new Incremented(5), new Incremented(7)], fixture.Context());

        var atTwo = await runtime.LoadAsOfSequenceAsync(streamId, 2);
        await Snapshots.PutAsync(streamId, atTwo.Version, atTwo.LastEventId!.Value, atTwo.State, SnapshotTime);

        var viaSnapshot = await runtime.LoadAsOfSequenceAsync(streamId, 2);
        var coldFold = await fixture.DurableRuntime().LoadAsOfSequenceAsync(streamId, 2);

        Assert.Equal(15, viaSnapshot.State.Total); // 3+5+7
        Assert.Equal(coldFold.State, viaSnapshot.State);
        Assert.Equal(2, viaSnapshot.Version);
    }

    /// <summary>
    /// The spine property: for ANY event sequence, ANY snapshot point, and ANY as-of point, the
    /// snapshot-accelerated as-of read equals the cold fold-to-as-of byte-for-byte — including the
    /// split where asOf is BELOW the earliest snapshot (the cold path) and where asOf is at/above it
    /// (the snapshot path). This is the as-of twin of <see cref="SnapshotEquivalenceProperties"/>.
    /// </summary>
    [Property(MaxTest = 30)]
    public void As_of_snapshot_read_equals_cold_fold_to_as_of(byte[] increments, byte snapshotSeed, byte asOfSeed)
    {
        var amounts = (increments.Length >= 1 ? increments : [1]).Select(b => 1 + (b % 9)).ToArray();
        var head = amounts.Length;                       // sequences 0 .. head-1
        var snapshotAt = snapshotSeed % head;            // place a snapshot at some sequence in [0, head-1]
        var asOf = asOfSeed % (head + 1);                // as-of point in [0, head] (head+1 lets it = head)

        var streamId = Guid.NewGuid();
        var runtime = fixture.DurableRuntime(withSnapshots: true);
        var snapshots = new SnapshotStore<CounterState>(fixture.SnapshotStorage, new JsonStateSerializer<CounterState>());

        // Append the whole stream.
        for (var i = 0; i < head; i++)
        {
            runtime.AppendAsync(streamId, i - 1, [new Incremented(amounts[i])], fixture.Context())
                .GetAwaiter().GetResult();
        }

        // Place a snapshot at snapshotAt (its state is the cold fold-to-snapshotAt — the live fold).
        var atSnapshot = runtime.LoadAsOfSequenceAsync(streamId, snapshotAt).GetAwaiter().GetResult();
        snapshots.PutAsync(streamId, atSnapshot.Version, atSnapshot.LastEventId!.Value, atSnapshot.State,
            SnapshotTime).GetAwaiter().GetResult();

        // The as-of read: when asOf < snapshotAt this is the COLD path (snapshot is the future, skipped);
        // when asOf >= snapshotAt it seeds from the snapshot and folds the tail. Either way it must equal
        // a cold fold-to-asOf on a snapshot-free runtime.
        var viaSnapshot = runtime.LoadAsOfSequenceAsync(streamId, asOf).GetAwaiter().GetResult();
        var coldFold = fixture.DurableRuntime().LoadAsOfSequenceAsync(streamId, asOf).GetAwaiter().GetResult();

        var expectedTotal = amounts.Take(Math.Min(asOf + 1, head)).Sum();
        Assert.Equal(expectedTotal, viaSnapshot.State.Total);
        Assert.Equal(coldFold.State, viaSnapshot.State);          // byte-for-byte
        Assert.Equal(coldFold.Version, viaSnapshot.Version);
        // The §P3 byte-identity also covers LastTransactionTime: in the empty-tail case the snapshot
        // path reports the snapshot's CreatedAt (the append-stamped transaction_time of its head event),
        // which equals the cold fold's TransactionTime for that same event — so the as-of read is
        // event-time-identical to a cold fold, not just state-identical.
        Assert.Equal(coldFold.LastTransactionTime, viaSnapshot.LastTransactionTime);
    }
}
