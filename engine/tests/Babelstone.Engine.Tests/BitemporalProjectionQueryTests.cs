using System.Text.Json;
using Babelstone.Engine;
using Babelstone.EventStore;
using Xunit;

namespace Babelstone.Engine.Tests;

/// <summary>
/// Pure unit tests for <see cref="BitemporalProjectionQuery{TState}"/> (ADR-PC-002 §P3) over an
/// in-memory <see cref="IProjectionStorage"/> fake. No database, no containers — these assert the
/// typed layer's behaviour (it delegates the temporal filtering to the byte store and maps the
/// returned <see cref="ProjectionRecord"/>s into typed <see cref="BeliefRow{TState}"/>s, preserving
/// both time axes), so they run in the default Docker-free engine CI lane. The SQL semantics of the
/// four canonical queries are covered against a real PostgreSQL in
/// <c>BitemporalProjectionQueryIntegrationTests</c>.
/// </summary>
public sealed class BitemporalProjectionQueryTests
{
    private const string Kind = "term_deposit.deposit_position";
    private static readonly DateTimeOffset ValidFrom = new(2026, 3, 15, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset RecordedThen = new(2026, 3, 15, 14, 23, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset CorrectedAt = new(2026, 5, 19, 9, 11, 0, TimeSpan.Zero);

    private readonly FakeProjectionStorage _storage = new();
    private readonly BitemporalProjectionQuery<Position> _query;

    public BitemporalProjectionQueryTests() =>
        _query = new BitemporalProjectionQuery<Position>(_storage, new JsonStateSerializer<Position>());

    [Fact]
    public async Task AsOf_maps_the_record_and_preserves_both_time_axes()
    {
        var streamId = Guid.NewGuid();
        _storage.AsOfResult = Record(streamId, 10_000_00, RecordedThen, supersededAt: CorrectedAt);

        var belief = await _query.AsOfAsync(streamId, Kind, ValidFrom, RecordedThen);

        Assert.NotNull(belief);
        Assert.Equal(10_000_00, belief.State.PrincipalCents);
        // The helper forwards the caller's coordinates to the byte store unchanged.
        Assert.Equal((streamId, Kind, ValidFrom, RecordedThen), _storage.AsOfCall);
        // Both axes survive the round-trip into the typed row.
        Assert.Equal(ValidFrom, belief.ValidFrom);
        Assert.Null(belief.ValidTo);
        Assert.Equal(RecordedThen, belief.RecordedAt);
        Assert.Equal(CorrectedAt, belief.SupersededAt);
    }

    [Fact]
    public async Task AsOf_returns_null_when_no_belief_covers_the_coordinates()
    {
        _storage.AsOfResult = null;
        Assert.Null(await _query.AsOfAsync(Guid.NewGuid(), Kind, ValidFrom, RecordedThen));
    }

    [Fact]
    public async Task CurrentBelief_maps_the_un_superseded_row()
    {
        var streamId = Guid.NewGuid();
        _storage.CurrentBeliefResult = Record(streamId, 100_000_00, CorrectedAt, supersededAt: null);

        var belief = await _query.CurrentBeliefAsync(streamId, Kind);

        Assert.NotNull(belief);
        Assert.Equal(100_000_00, belief.State.PrincipalCents);
        Assert.Null(belief.SupersededAt); // the current belief carries no supersession stamp
    }

    [Fact]
    public async Task CurrentBelief_returns_null_when_absent()
    {
        _storage.CurrentBeliefResult = null;
        Assert.Null(await _query.CurrentBeliefAsync(Guid.NewGuid(), Kind));
    }

    [Fact]
    public async Task HistoryOf_maps_every_row_preserving_the_byte_store_order()
    {
        var streamId = Guid.NewGuid();
        // The byte store returns the belief line in belief-time order; the helper preserves it.
        _storage.HistoryResult =
        [
            Record(streamId, 10_000_00, RecordedThen, supersededAt: CorrectedAt),
            Record(streamId, 100_000_00, CorrectedAt, supersededAt: null),
        ];

        var history = await _query.HistoryOfAsync(streamId, Kind);

        Assert.Equal(2, history.Count);
        Assert.Equal(10_000_00, history[0].State.PrincipalCents);
        Assert.Equal(CorrectedAt, history[0].SupersededAt);
        Assert.Equal(100_000_00, history[1].State.PrincipalCents);
        Assert.Null(history[1].SupersededAt);
    }

    [Fact]
    public async Task HistoryOf_is_empty_when_the_byte_store_returns_no_rows()
    {
        _storage.HistoryResult = [];
        Assert.Empty(await _query.HistoryOfAsync(Guid.NewGuid(), Kind));
    }

    // --- helpers ---

    private static ProjectionRecord Record(Guid streamId, long principalCents, DateTimeOffset recordedAt, DateTimeOffset? supersededAt) =>
        new(
            StreamId: streamId,
            ProjectionKind: Kind,
            SourceSequence: 0,
            ValidFrom: ValidFrom,
            ValidTo: null,
            RecordedAt: recordedAt,
            SupersededAt: supersededAt,
            StructuralPayload: JsonSerializer.SerializeToUtf8Bytes(new Position(principalCents)),
            PiiCiphertext: ReadOnlyMemory<byte>.Empty);

    private sealed record Position(long PrincipalCents);

    /// <summary>
    /// An in-memory <see cref="IProjectionStorage"/> that records the read coordinates and returns
    /// canned rows — enough to assert the typed helper's delegation and mapping without a database.
    /// The write-path members are unused here (the helper is read-only).
    /// </summary>
    private sealed class FakeProjectionStorage : IProjectionStorage
    {
        public ProjectionRecord? AsOfResult { get; set; }
        public ProjectionRecord? CurrentBeliefResult { get; set; }
        public IReadOnlyList<ProjectionRecord> HistoryResult { get; set; } = [];
        public (Guid Stream, string Kind, DateTimeOffset ValidTime, DateTimeOffset KnownAt)? AsOfCall { get; private set; }

        public Task<ProjectionRecord?> ReadAsOfAsync(
            Guid streamId, string projectionKind, DateTimeOffset validTime, DateTimeOffset knownAt, CancellationToken ct = default)
        {
            AsOfCall = (streamId, projectionKind, validTime, knownAt);
            return Task.FromResult(AsOfResult);
        }

        public Task<ProjectionRecord?> ReadCurrentBeliefAsync(Guid streamId, string projectionKind, CancellationToken ct = default) =>
            Task.FromResult(CurrentBeliefResult);

        public Task<IReadOnlyList<ProjectionRecord>> ReadHistoryOfAsync(Guid streamId, string projectionKind, CancellationToken ct = default) =>
            Task.FromResult(HistoryResult);

        public Task WriteAsync(ProjectionRecord record, CancellationToken ct = default) =>
            throw new NotSupportedException("The query helper only reads.");

        public Task SupersedeAsync(Guid streamId, string projectionKind, DateTimeOffset supersededAt, CancellationToken ct = default) =>
            throw new NotSupportedException("The query helper only reads.");

        public Task SupersedeAndWriteAsync(ProjectionRecord record, CancellationToken ct = default) =>
            throw new NotSupportedException("The query helper only reads.");

        public Task SupersedeAllAsync(string projectionKind, DateTimeOffset supersededAt, CancellationToken ct = default) =>
            throw new NotSupportedException("The query helper only reads.");
    }
}
