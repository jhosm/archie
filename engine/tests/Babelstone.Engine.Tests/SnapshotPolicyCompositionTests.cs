using Babelstone.EventStore;
using Xunit;

namespace Babelstone.Engine.Tests;

/// <summary>
/// A.12 composing snapshot policy (bd babelstone-e6fr.12 / ADR-PC-003 §P2, event-store §8.1). The
/// snapshot policy composes THREE independent triggers — every-N events, lifecycle boundaries,
/// calendar boundaries — and takes a snapshot if ANY fires. This proves all three fire IN THE RUNNING
/// ENGINE (the post-commit write path), that the per-N threshold is per-family/host configurable, and
/// that the boundary triggers fire even when the per-N count is nowhere near its threshold. Real PG18.
/// </summary>
[Trait("Category", "Integration")]
public sealed class SnapshotPolicyCompositionTests(EngineFixture fixture) : IClassFixture<EngineFixture>
{
    private SnapshotStore<CounterState> Snapshots =>
        new(fixture.SnapshotStorage, new JsonStateSerializer<CounterState>());

    // ── Trigger 1: per-N events ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Per_n_trigger_fires_when_the_un_snapshotted_count_crosses_the_threshold()
    {
        // every-3 policy, no calendar trigger: three events cross the per-N threshold, so a snapshot
        // is written at the head even though no event is a lifecycle boundary.
        var runtime = fixture.SnapshottingRuntime(everyNEvents: 3);
        var streamId = Guid.NewGuid();

        await runtime.AppendAsync(
            streamId, -1, [new Incremented(1), new Incremented(2), new Incremented(3)], fixture.Context());

        var written = await Snapshots.TryGetAsync(streamId);
        Assert.NotNull(written);
        Assert.Equal(2, written.AtSequence); // head of the 3-event stream (sequences 0,1,2)
        Assert.Equal(6, written.State.Total);
    }

    [Fact]
    public async Task Per_n_threshold_is_configurable_per_family_so_a_higher_threshold_does_not_fire()
    {
        // The SAME 3-event append under an every-100 threshold writes NO snapshot — the per-N cadence is
        // host/family config (Engine:SnapshotEveryNEvents), honoured here by the policy's threshold arg.
        var runtime = fixture.SnapshottingRuntime(everyNEvents: 100);
        var streamId = Guid.NewGuid();

        await runtime.AppendAsync(
            streamId, -1, [new Incremented(1), new Incremented(2), new Incremented(3)], fixture.Context());

        Assert.Null(await Snapshots.TryGetAsync(streamId));
    }

    // ── Trigger 2: lifecycle boundary ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Lifecycle_boundary_trigger_fires_below_the_per_n_threshold()
    {
        // every-100 threshold (the per-N trigger will NOT fire on a 1-event stream), but the single event
        // is a lifecycle boundary (LifecycleIncremented overrides IsLifecycleBoundary) — so the composing
        // policy still snapshots. This proves the family-supplied lifecycle flag fires the trigger in the
        // running engine, independent of the event count.
        var runtime = fixture.SnapshottingRuntime(everyNEvents: 100);
        var streamId = Guid.NewGuid();

        await runtime.AppendAsync(streamId, -1, [new LifecycleIncremented(7)], fixture.Context());

        var written = await Snapshots.TryGetAsync(streamId);
        Assert.NotNull(written);
        Assert.Equal(0, written.AtSequence);
        Assert.Equal(7, written.State.Total);
    }

    [Fact]
    public async Task An_ordinary_event_below_the_threshold_is_no_lifecycle_boundary_so_no_snapshot()
    {
        // The control for the lifecycle test: an ordinary Incremented below the per-N threshold and with
        // no calendar trigger writes NO snapshot — proving the lifecycle trigger above fired BECAUSE of
        // the boundary flag, not spuriously.
        var runtime = fixture.SnapshottingRuntime(everyNEvents: 100);
        var streamId = Guid.NewGuid();

        await runtime.AppendAsync(streamId, -1, [new Incremented(7)], fixture.Context());

        Assert.Null(await Snapshots.TryGetAsync(streamId));
    }

    // ── Trigger 3: calendar boundary ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Calendar_boundary_trigger_fires_when_an_append_crosses_a_month_below_the_threshold()
    {
        // every-100 threshold (per-N will not fire), Month-granularity calendar policy, ordinary events
        // (no lifecycle flag). A settable clock places the FIRST append in January and the SECOND in
        // February — so the second append crosses a month boundary and the composing policy snapshots,
        // even though only two events exist and neither is a lifecycle event.
        var clock = new SettableTimeProvider(new DateTimeOffset(2026, 1, 15, 0, 0, 0, TimeSpan.Zero));
        var runtime = fixture.SnapshottingRuntime(
            everyNEvents: 100,
            calendarBoundaryPolicy: new CalendarBoundaryPolicy(CalendarGranularity.Month),
            clock: clock);
        var streamId = Guid.NewGuid();

        // First append (January): the per-N trigger does not fire and there is no previous event to cross
        // a calendar boundary against, so NO snapshot yet.
        await runtime.AppendAsync(streamId, -1, [new Incremented(1)], fixture.Context());
        Assert.Null(await Snapshots.TryGetAsync(streamId));

        // Advance the clock into February, then append: the append's transaction_time is a later month
        // than the previous head's → calendar boundary crossed → snapshot written at the new head.
        clock.Now = new DateTimeOffset(2026, 2, 3, 0, 0, 0, TimeSpan.Zero);
        await runtime.AppendAsync(streamId, 0, [new Incremented(2)], fixture.Context());

        var written = await Snapshots.TryGetAsync(streamId);
        Assert.NotNull(written);
        Assert.Equal(1, written.AtSequence);
        Assert.Equal(3, written.State.Total);
    }

    [Fact]
    public async Task Calendar_trigger_does_not_fire_within_the_same_month()
    {
        // The control for the calendar test: two appends in the SAME month, below the per-N threshold,
        // no lifecycle flag → NO snapshot. Proves the calendar trigger above fired on the month CROSSING,
        // not on every second append.
        var clock = new SettableTimeProvider(new DateTimeOffset(2026, 1, 5, 0, 0, 0, TimeSpan.Zero));
        var runtime = fixture.SnapshottingRuntime(
            everyNEvents: 100,
            calendarBoundaryPolicy: new CalendarBoundaryPolicy(CalendarGranularity.Month),
            clock: clock);
        var streamId = Guid.NewGuid();

        await runtime.AppendAsync(streamId, -1, [new Incremented(1)], fixture.Context());
        clock.Now = new DateTimeOffset(2026, 1, 25, 0, 0, 0, TimeSpan.Zero); // still January
        await runtime.AppendAsync(streamId, 0, [new Incremented(2)], fixture.Context());

        Assert.Null(await Snapshots.TryGetAsync(streamId));
    }

    // ── Fail-soft composition ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_failed_snapshot_write_never_fails_the_append_even_when_a_boundary_fires()
    {
        // ADR-PC-003 §P2 acceptance: a FAILED snapshot write must NOT fail the append. Drive a lifecycle
        // boundary (so the policy definitely fires) against a storage that always throws on write — the
        // append must still succeed and return the head; the cold fold reconstructs the committed event.
        Exception? surfaced = null;
        var runtime = fixture.SnapshottingRuntime(
            everyNEvents: 100, onSnapshotError: ex => surfaced = ex, storage: new ThrowingSnapshotStorage());
        var streamId = Guid.NewGuid();

        var head = await runtime.AppendAsync(streamId, -1, [new LifecycleIncremented(9)], fixture.Context());

        Assert.Equal(0, head);
        Assert.NotNull(surfaced); // the fail-soft sink saw the failure
        var coldFold = await fixture.DurableRuntime().LoadAsync(streamId);
        Assert.Equal(9, coldFold.State.Total); // the committed event is the book of record
    }

    /// <summary>A snapshot storage that always throws on write — exercises the fail-soft post-commit path.</summary>
    private sealed class ThrowingSnapshotStorage : ISnapshotStorage
    {
        public Task<SnapshotRecord?> TryGetLatestAsync(Guid streamId, CancellationToken ct = default)
            => Task.FromResult<SnapshotRecord?>(null);

        public Task<SnapshotRecord?> TryGetAtOrBeforeAsync(
            Guid streamId, long atOrBeforeSequence, CancellationToken ct = default)
            => Task.FromResult<SnapshotRecord?>(null);

        public Task PutAsync(SnapshotRecord snapshot, CancellationToken ct = default)
            => throw new InvalidOperationException("snapshot store unavailable");

        public Task<int> DiscardAsync(Guid streamId, CancellationToken ct = default)
            => Task.FromResult(0);
    }
}

/// <summary>
/// A.12 pure-unit coverage of the two composing policy types (no database) — the CountBasedSnapshotPolicy
/// OR-composition (ADR-PC-003 §P2) and the CalendarBoundaryPolicy period comparison.
/// </summary>
public sealed class SnapshotPolicyUnitTests
{
    [Theory]
    [InlineData(2, false, false, 3, false)]  // below threshold, no boundary → no snapshot
    [InlineData(4, false, false, 3, true)]   // above threshold fires on the per-N count alone
    [InlineData(1, true, false, 3, true)]    // lifecycle boundary fires below threshold
    [InlineData(1, false, true, 3, true)]    // calendar boundary fires below threshold
    [InlineData(3, false, false, 3, true)]   // exactly at threshold fires
    public void Count_based_policy_ors_the_three_triggers(
        long eventsSince, bool lifecycle, bool calendar, long threshold, bool expected)
    {
        var policy = new CountBasedSnapshotPolicy(threshold);
        var ctx = new SnapshotContext(eventsSince, lifecycle, calendar);
        Assert.Equal(expected, policy.ShouldSnapshot(ctx));
    }

    [Fact]
    public void Calendar_policy_fires_on_a_later_month_not_within_one()
    {
        var policy = new CalendarBoundaryPolicy(CalendarGranularity.Month);
        var janEarly = new DateTimeOffset(2026, 1, 5, 0, 0, 0, TimeSpan.Zero);
        var janLate = new DateTimeOffset(2026, 1, 31, 23, 0, 0, TimeSpan.Zero);
        var feb = new DateTimeOffset(2026, 2, 1, 1, 0, 0, TimeSpan.Zero);

        Assert.True(policy.IsActive);
        Assert.True(policy.CrossedBoundary(janLate, feb));        // crosses into February
        Assert.False(policy.CrossedBoundary(janEarly, janLate));  // both in January — no crossing
        Assert.False(policy.CrossedBoundary(null, feb));          // no previous event ⇒ no crossing
    }

    [Fact]
    public void Calendar_policy_year_granularity_fires_only_on_a_later_year()
    {
        var policy = new CalendarBoundaryPolicy(CalendarGranularity.Year);
        var dec2026 = new DateTimeOffset(2026, 12, 31, 0, 0, 0, TimeSpan.Zero);
        var jan2027 = new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var feb2026 = new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero);

        Assert.True(policy.CrossedBoundary(dec2026, jan2027));  // crosses the year
        Assert.False(policy.CrossedBoundary(dec2026, feb2026)); // a later month, same year, is no year crossing
    }

    [Fact]
    public void Calendar_policy_none_granularity_is_inactive_and_never_fires()
    {
        var policy = new CalendarBoundaryPolicy(CalendarGranularity.None);
        Assert.False(policy.IsActive);
        Assert.False(policy.CrossedBoundary(
            new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2027, 6, 1, 0, 0, 0, TimeSpan.Zero)));
    }
}
