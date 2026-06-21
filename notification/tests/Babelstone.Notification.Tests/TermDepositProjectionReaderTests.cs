using System.Text.Json;
using Babelstone.EventStore;
using Babelstone.Families.TermDeposit;
using Babelstone.FinancialTypes;
using Babelstone.Notification;
using Xunit;

namespace Babelstone.Notification.Tests;

/// <summary>
/// Pure unit tests for <see cref="TermDepositProjectionReader"/> — the notification service's read
/// window onto the engine's term-deposit projections (ADR-IC-005 CQRS read surface). Docker-free:
/// they run over an in-memory <see cref="IProjectionStorage"/> fake (the same pattern as the
/// engine's <c>BitemporalProjectionQueryTests</c>), asserting that the reader (1) keys each of the
/// three reads on the writer-side family-prefixed projection-kind discriminator declared in
/// <see cref="TermDepositProjectionModule"/> (so the kind string cannot drift from the producer),
/// (2) returns the typed family state for the current belief, and (3) returns <see langword="null"/>
/// when no projection has materialised. The end-to-end read against a real PostgreSQL projections
/// table is the engine's <c>BitemporalProjectionQueryIntegrationTests</c>' job.
/// </summary>
public sealed class TermDepositProjectionReaderTests
{
    private static readonly DateTimeOffset ValidFrom = new(2026, 3, 15, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset RecordedAt = new(2026, 3, 15, 14, 23, 0, TimeSpan.Zero);

    private readonly FakeProjectionStorage _storage = new();
    private readonly TermDepositProjectionReader _reader;

    public TermDepositProjectionReaderTests() => _reader = new TermDepositProjectionReader(_storage);

    [Fact]
    public async Task ReadMaturityCalendar_reads_the_current_belief_on_the_maturity_calendar_kind()
    {
        var streamId = Guid.NewGuid();
        var calendar = new MaturityCalendar(
        [
            new MaturityCalendarEntry(MaturityEventKind.Constituted, new DateOnly(2026, 3, 15)),
            new MaturityCalendarEntry(MaturityEventKind.ScheduledMaturity, new DateOnly(2027, 3, 15)),
        ]);
        _storage.CurrentBelief = Record(streamId, TermDepositProjectionModule.MaturityCalendarKind, calendar);

        var result = await _reader.ReadMaturityCalendarAsync(streamId);

        Assert.NotNull(result);
        Assert.Equal(2, result.Entries.Count);
        Assert.Equal(MaturityEventKind.ScheduledMaturity, result.Entries[1].Kind);
        // The reader keys the read on the writer-side family-prefixed discriminator — not a literal.
        Assert.Equal((streamId, TermDepositProjectionModule.MaturityCalendarKind), _storage.LastCall);
    }

    [Fact]
    public async Task ReadAccrualSchedule_reads_the_current_belief_on_the_accrual_schedule_kind()
    {
        var streamId = Guid.NewGuid();
        var schedule = new AccrualSchedule(
            [new AccrualEntry(new DateOnly(2026, 6, 15), Money.FromCents(1_234), "accrued")],
            Money.FromCents(1_234));
        _storage.CurrentBelief = Record(streamId, TermDepositProjectionModule.AccrualScheduleKind, schedule);

        var result = await _reader.ReadAccrualScheduleAsync(streamId);

        Assert.NotNull(result);
        Assert.Equal(1_234, result.TotalGrossAccrued.Cents);
        Assert.Equal((streamId, TermDepositProjectionModule.AccrualScheduleKind), _storage.LastCall);
    }

    [Fact]
    public async Task ReadWithholdingLedger_reads_the_current_belief_on_the_withholding_ledger_kind()
    {
        var streamId = Guid.NewGuid();
        var ledger = new WithholdingLedger(
            [new WithholdingEntry(Money.FromCents(1_000), Money.FromCents(280), Money.FromCents(720), "withholding")],
            Money.FromCents(1_000), Money.FromCents(280), Money.FromCents(720));
        _storage.CurrentBelief = Record(streamId, TermDepositProjectionModule.WithholdingLedgerKind, ledger);

        var result = await _reader.ReadWithholdingLedgerAsync(streamId);

        Assert.NotNull(result);
        Assert.Equal(280, result.TotalTax.Cents);
        Assert.Equal((streamId, TermDepositProjectionModule.WithholdingLedgerKind), _storage.LastCall);
    }

    [Theory]
    [InlineData("maturity")]
    [InlineData("accrual")]
    [InlineData("withholding")]
    public async Task A_read_returns_null_when_no_projection_has_materialised(string which)
    {
        _storage.CurrentBelief = null; // no current-belief row for the stream/kind pair
        var streamId = Guid.NewGuid();

        object? result = which switch
        {
            "maturity" => await _reader.ReadMaturityCalendarAsync(streamId),
            "accrual" => await _reader.ReadAccrualScheduleAsync(streamId),
            _ => await _reader.ReadWithholdingLedgerAsync(streamId),
        };

        Assert.Null(result);
    }

    // --- helpers ---

    private static ProjectionRecord Record<TState>(Guid streamId, string kind, TState state) =>
        new(
            StreamId: streamId,
            ProjectionKind: kind,
            SourceSequence: 0,
            ValidFrom: ValidFrom,
            ValidTo: null,
            RecordedAt: RecordedAt,
            SupersededAt: null,
            // The reader deserializes with JsonStateSerializer<TState> (the engine's deterministic
            // codec); serialize the fixture the same way so the round-trip matches the writer side.
            StructuralPayload: JsonSerializer.SerializeToUtf8Bytes(state),
            PiiCiphertext: ReadOnlyMemory<byte>.Empty);

    /// <summary>
    /// An in-memory <see cref="IProjectionStorage"/> that records the current-belief read
    /// coordinates and returns a canned row — enough to assert the reader's kind keying and typed
    /// mapping without a database. Only the current-belief read is exercised (the reader reads the
    /// currently-known projection, never a historical/counterfactual belief slice); the other
    /// members fail loud if touched.
    /// </summary>
    private sealed class FakeProjectionStorage : IProjectionStorage
    {
        public ProjectionRecord? CurrentBelief { get; set; }
        public (Guid Stream, string Kind)? LastCall { get; private set; }

        public Task<ProjectionRecord?> ReadCurrentBeliefAsync(Guid streamId, string projectionKind, CancellationToken ct = default)
        {
            LastCall = (streamId, projectionKind);
            return Task.FromResult(CurrentBelief);
        }

        public Task<ProjectionRecord?> ReadAsOfAsync(
            Guid streamId, string projectionKind, DateTimeOffset validTime, DateTimeOffset knownAt, CancellationToken ct = default) =>
            throw new NotSupportedException("The notification reader reads only the current belief.");

        public Task<IReadOnlyList<ProjectionRecord>> ReadHistoryOfAsync(Guid streamId, string projectionKind, CancellationToken ct = default) =>
            throw new NotSupportedException("The notification reader reads only the current belief.");

        public Task WriteAsync(ProjectionRecord record, CancellationToken ct = default) =>
            throw new NotSupportedException("The notification reader is read-only (SELECT-only grant).");

        public Task SupersedeAsync(Guid streamId, string projectionKind, DateTimeOffset supersededAt, CancellationToken ct = default) =>
            throw new NotSupportedException("The notification reader is read-only (SELECT-only grant).");

        public Task SupersedeAndWriteAsync(ProjectionRecord record, CancellationToken ct = default) =>
            throw new NotSupportedException("The notification reader is read-only (SELECT-only grant).");

        public Task SupersedeAllAsync(string projectionKind, DateTimeOffset supersededAt, CancellationToken ct = default) =>
            throw new NotSupportedException("The notification reader is read-only (SELECT-only grant).");
    }
}
