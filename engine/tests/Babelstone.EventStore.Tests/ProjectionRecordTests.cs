using Babelstone.EventStore;
using Xunit;

namespace Babelstone.EventStore.Tests;

/// <summary>
/// Pure unit tests for <see cref="ProjectionRecord"/> (ADR-PC-002 §P1/§P2). No database,
/// no containers — these assert the in-memory bitemporal semantics of the record itself,
/// so they run in the default (Docker-free) engine CI lane.
/// </summary>
public sealed class ProjectionRecordTests
{
    private static readonly DateTimeOffset ValidFrom = new(2026, 5, 31, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset RecordedAt = new(2026, 5, 31, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Construction_round_trips_all_fields()
    {
        var streamId = Guid.NewGuid();
        var validTo = ValidFrom.AddDays(365);
        var supersededAt = RecordedAt.AddHours(1);
        var structural = new byte[] { 1, 2, 3, 4 };
        var ciphertext = new byte[] { 9, 8, 7 };

        var record = new ProjectionRecord(
            StreamId: streamId,
            ValidFrom: ValidFrom,
            ValidTo: validTo,
            RecordedAt: RecordedAt,
            SupersededAt: supersededAt,
            StructuralPayload: structural,
            PiiCiphertext: ciphertext);

        Assert.Equal(streamId, record.StreamId);
        Assert.Equal(ValidFrom, record.ValidFrom);
        Assert.Equal(validTo, record.ValidTo);
        Assert.Equal(RecordedAt, record.RecordedAt);
        Assert.Equal(supersededAt, record.SupersededAt);
        Assert.True(record.StructuralPayload.Span.SequenceEqual(structural));
        Assert.True(record.PiiCiphertext.Span.SequenceEqual(ciphertext));
    }

    [Fact]
    public void SupersededAt_null_marks_the_current_belief()
    {
        // ADR-PC-002 §P2 — superseded_at IS NULL is the currently-believed row.
        var current = NewRecord(supersededAt: null);
        var superseded = NewRecord(supersededAt: RecordedAt.AddHours(2));

        Assert.Null(current.SupersededAt);
        Assert.NotNull(superseded.SupersededAt);
    }

    [Fact]
    public void ValidTo_null_means_open_ended_world_time()
    {
        // ADR-PC-002 §P1 — valid_to NULL is an open-ended world-time slice.
        var openEnded = NewRecord(validTo: null);
        var bounded = NewRecord(validTo: ValidFrom.AddDays(30));

        Assert.Null(openEnded.ValidTo);
        Assert.NotNull(bounded.ValidTo);
    }

    [Fact]
    public void PiiCiphertext_defaults_to_empty_when_no_pii_present()
    {
        // ADR-PC-004 §P2 — the PII envelope is empty until PII is added by a later task.
        var record = NewRecord(piiCiphertext: ReadOnlyMemory<byte>.Empty);

        Assert.True(record.PiiCiphertext.IsEmpty);
    }

    [Fact]
    public void Record_equality_uses_ReadOnlyMemory_structure_not_byte_contents()
    {
        // Record equality compares ReadOnlyMemory<byte> by its struct fields (object,
        // index, length): two records that share a backing array are equal; two records
        // with distinct arrays of identical bytes are NOT. That is the idiom callers must
        // know — assert payload contents with Span.SequenceEqual, not record ==.
        var streamId = Guid.NewGuid();
        var shared = new byte[] { 1, 2, 3 };
        var copy = new byte[] { 1, 2, 3 };

        var a = RecordFor(streamId, shared);
        var sameBacking = RecordFor(streamId, shared);
        var differentBacking = RecordFor(streamId, copy);

        Assert.Equal(a, sameBacking);
        Assert.NotEqual(a, differentBacking);
        Assert.True(a.StructuralPayload.Span.SequenceEqual(differentBacking.StructuralPayload.Span));
    }

    private static ProjectionRecord RecordFor(Guid streamId, byte[] structural) =>
        new(
            StreamId: streamId,
            ValidFrom: ValidFrom,
            ValidTo: null,
            RecordedAt: RecordedAt,
            SupersededAt: null,
            StructuralPayload: structural,
            PiiCiphertext: ReadOnlyMemory<byte>.Empty);

    private static ProjectionRecord NewRecord(
        DateTimeOffset? validTo = null,
        DateTimeOffset? supersededAt = null,
        byte[]? structural = null,
        ReadOnlyMemory<byte>? piiCiphertext = null) =>
        new(
            StreamId: Guid.NewGuid(),
            ValidFrom: ValidFrom,
            ValidTo: validTo,
            RecordedAt: RecordedAt,
            SupersededAt: supersededAt,
            StructuralPayload: structural ?? new byte[] { 1, 2, 3 },
            PiiCiphertext: piiCiphertext ?? ReadOnlyMemory<byte>.Empty);
}
