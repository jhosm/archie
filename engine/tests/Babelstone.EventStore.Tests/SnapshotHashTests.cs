using Babelstone.EventStore;
using Xunit;

namespace Babelstone.EventStore.Tests;

/// <summary>
/// The §8.3 snapshot hash is pure and deterministic — these run in the default lane.
/// </summary>
public sealed class SnapshotHashTests
{
    private static readonly Guid EventId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly byte[] State = [0x10, 0x20, 0x30];

    [Fact]
    public void Same_inputs_produce_the_same_digest()
    {
        Assert.Equal(SnapshotHash.Compute(State, EventId), SnapshotHash.Compute(State, EventId));
    }

    [Fact]
    public void Different_last_event_id_changes_the_digest()
    {
        // This is the §8.3 property: the hash detects a snapshot taken at a different
        // point in history even when the serialized state bytes are identical.
        var other = Guid.Parse("22222222-2222-2222-2222-222222222222");
        Assert.NotEqual(SnapshotHash.Compute(State, EventId), SnapshotHash.Compute(State, other));
    }

    [Fact]
    public void Different_state_changes_the_digest()
    {
        Assert.NotEqual(SnapshotHash.Compute(State, EventId), SnapshotHash.Compute([0x10, 0x20, 0x31], EventId));
    }

    [Fact]
    public void Digest_is_64_hex_chars()
    {
        var digest = SnapshotHash.Compute(State, EventId);
        Assert.Equal(64, digest.Length);
        Assert.True(digest.All(Uri.IsHexDigit));
    }
}
