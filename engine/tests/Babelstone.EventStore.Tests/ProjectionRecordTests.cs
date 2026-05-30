using Babelstone.EventStore;
using Xunit;

namespace Babelstone.EventStore.Tests;

/// <summary>
/// Pure unit tests for <see cref="ProjectionRecord"/> field semantics.
/// No database, no Docker — runs in the default engine CI lane.
/// </summary>
public sealed class ProjectionRecordTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset T1 = new(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly byte[] StructuralBytes = [0x01, 0x02, 0x03];
    private static readonly byte[] PiiBytes        = [0xAA, 0xBB];

    [Fact]
    public void Field_round_trip_preserves_all_values()
    {
        var streamId = Guid.NewGuid();
        var record = new ProjectionRecord(
            StreamId:          streamId,
            ValidFrom:         T0,
            ValidTo:           T1,
            RecordedAt:        T0,
            SupersededAt:      null,
            StructuralPayload: StructuralBytes,
            PiiCiphertext:     PiiBytes);

        Assert.Equal(streamId, record.StreamId);
        Assert.Equal(T0,       record.ValidFrom);
        Assert.Equal(T1,       record.ValidTo);
        Assert.Equal(T0,       record.RecordedAt);
        Assert.Null(record.SupersededAt);
        Assert.Equal(StructuralBytes, record.StructuralPayload.ToArray());
        Assert.Equal(PiiBytes,        record.PiiCiphertext.ToArray());
    }

    [Fact]
    public void SupersededAt_null_means_currently_believed()
    {
        // The "currently-believed" semantic: superseded_at IS NULL in the partial
        // index (ADR-PC-002 §P2) maps to SupersededAt == null on the record.
        var record = new ProjectionRecord(
            StreamId:          Guid.NewGuid(),
            ValidFrom:         T0,
            ValidTo:           null,
            RecordedAt:        T0,
            SupersededAt:      null,
            StructuralPayload: StructuralBytes,
            PiiCiphertext:     ReadOnlyMemory<byte>.Empty);

        Assert.Null(record.SupersededAt);
    }

    [Fact]
    public void SupersededAt_non_null_means_belief_was_corrected()
    {
        var supersededAt = T0.AddHours(1);
        var record = new ProjectionRecord(
            StreamId:          Guid.NewGuid(),
            ValidFrom:         T0,
            ValidTo:           null,
            RecordedAt:        T0,
            SupersededAt:      supersededAt,
            StructuralPayload: StructuralBytes,
            PiiCiphertext:     ReadOnlyMemory<byte>.Empty);

        Assert.Equal(supersededAt, record.SupersededAt);
    }

    [Fact]
    public void ValidTo_null_means_open_ended_in_world_time()
    {
        // valid_to NULL = the position is still open in real-world time
        // (ADR-PC-002 §P1 — world-time open interval).
        var record = new ProjectionRecord(
            StreamId:          Guid.NewGuid(),
            ValidFrom:         T0,
            ValidTo:           null,
            RecordedAt:        T0,
            SupersededAt:      null,
            StructuralPayload: StructuralBytes,
            PiiCiphertext:     ReadOnlyMemory<byte>.Empty);

        Assert.Null(record.ValidTo);
    }

    [Fact]
    public void ValidTo_non_null_closes_the_world_time_interval()
    {
        var record = new ProjectionRecord(
            StreamId:          Guid.NewGuid(),
            ValidFrom:         T0,
            ValidTo:           T1,
            RecordedAt:        T0,
            SupersededAt:      null,
            StructuralPayload: StructuralBytes,
            PiiCiphertext:     ReadOnlyMemory<byte>.Empty);

        Assert.Equal(T1, record.ValidTo);
    }

    [Fact]
    public void Empty_pii_ciphertext_is_valid_for_pre_pii_work()
    {
        // PII column is nullable in the DB (added in later work); an empty
        // ReadOnlyMemory<byte> is the in-process representation of that absence.
        var record = new ProjectionRecord(
            StreamId:          Guid.NewGuid(),
            ValidFrom:         T0,
            ValidTo:           null,
            RecordedAt:        T0,
            SupersededAt:      null,
            StructuralPayload: StructuralBytes,
            PiiCiphertext:     ReadOnlyMemory<byte>.Empty);

        Assert.Equal(0, record.PiiCiphertext.Length);
    }

    [Fact]
    public void Records_with_same_values_are_equal()
    {
        var streamId = Guid.NewGuid();
        var a = new ProjectionRecord(streamId, T0, T1, T0, null, StructuralBytes, PiiBytes);
        var b = new ProjectionRecord(streamId, T0, T1, T0, null, StructuralBytes, PiiBytes);

        // ReadOnlyMemory<byte> uses reference equality in the default record Equals,
        // so we check field-by-field rather than record equality here.
        Assert.Equal(a.StreamId,     b.StreamId);
        Assert.Equal(a.ValidFrom,    b.ValidFrom);
        Assert.Equal(a.ValidTo,      b.ValidTo);
        Assert.Equal(a.RecordedAt,   b.RecordedAt);
        Assert.Equal(a.SupersededAt, b.SupersededAt);
        Assert.Equal(a.StructuralPayload.ToArray(), b.StructuralPayload.ToArray());
        Assert.Equal(a.PiiCiphertext.ToArray(),     b.PiiCiphertext.ToArray());
    }
}
