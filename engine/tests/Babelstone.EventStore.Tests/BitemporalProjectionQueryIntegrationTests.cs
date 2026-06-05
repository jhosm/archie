using System.Text.Json;
using Babelstone.Engine;
using Npgsql;
using Xunit;

namespace Babelstone.EventStore.Tests;

/// <summary>
/// Integration tests for the typed bitemporal query helper (ADR-PC-002 §P3) over a real PostgreSQL
/// projection store. They exercise the FOUR canonical time-dimensional capabilities
/// (event-store §2) against a genuine bitemporal history — an initial belief plus a RETROACTIVE
/// correction, so as-of and current-belief genuinely differ:
/// <list type="number">
/// <item>#1 as-of — the state on a valid-time, as known at a transaction-time;</item>
/// <item>#2 audit trail — the belief line (HistoryOf), how belief changed;</item>
/// <item>#3 counterfactual replay — the disavowed vs corrected belief (AsOf across the correction);</item>
/// <item>#4 forward projection — a future valid-time under the current belief.</item>
/// </list>
/// The helper composes the byte store's two-axis reads (<see cref="IProjectionStorage.ReadAsOfAsync"/>,
/// <see cref="IProjectionStorage.ReadHistoryOfAsync"/>, <see cref="IProjectionStorage.ReadCurrentBeliefAsync"/>),
/// so these tests cover both the typed layer and the SQL underneath.
/// </summary>
[Trait("Category", "Integration")]
public sealed class BitemporalProjectionQueryIntegrationTests(PostgresEventStoreFixture fixture)
    : IClassFixture<PostgresEventStoreFixture>
{
    private const string Kind = "term_deposit.deposit_position";

    private readonly PostgresProjectionStore _store = new(fixture.ConnectionString);

    private readonly BitemporalProjectionQuery<DepositPosition> _query =
        new(new PostgresProjectionStore(fixture.ConnectionString), new JsonStateSerializer<DepositPosition>());

    // A deposit's principal is recorded as €10,000 on 2026-03-15 (clerk-data-entry error); the
    // true principal was €100,000; corrected on 2026-05-19 via a DepositCorrected event. The
    // worked example from ADR-PC-002 §Gate / event-store §6.4.
    private static readonly DateTimeOffset ValidFrom = new(2026, 3, 15, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset RecordedThen = new(2026, 3, 15, 14, 23, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset CorrectedAt = new(2026, 5, 19, 9, 11, 0, TimeSpan.Zero);

    private const long WrongPrincipal = 10_000_00;
    private const long TruePrincipal = 100_000_00;

    private async Task ResetAsync()
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("TRUNCATE projections;", connection);
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// Seeds the bitemporal history of one deposit position: the original (wrong) belief, then a
    /// forced correction that supersedes it (the supersede-then-insert pair, ADR-PC-002 §P2). The
    /// world-time slice is open-ended (valid_to NULL) on both — the position holds from
    /// constitution onward; only the believed VALUE differs across belief-time.
    /// </summary>
    private async Task<Guid> SeedCorrectedHistoryAsync()
    {
        await ResetAsync();
        var streamId = Guid.NewGuid();

        // What we knew then: €10,000 from 2026-03-15 onward, recorded 2026-03-15T14:23.
        await _store.WriteAsync(Record(streamId, WrongPrincipal, RecordedThen, sourceSequence: 0));

        // The retroactive correction on 2026-05-19: supersede the wrong belief AT correction time,
        // insert the corrected €100,000 belief — atomically (the steady-state §P2 update).
        await _store.SupersedeAndWriteAsync(Record(streamId, TruePrincipal, CorrectedAt, sourceSequence: 1));

        return streamId;
    }

    // ---------- #1 As-of: the state on a valid-time, as known at a transaction-time ----------

    [Fact]
    public async Task AsOf_capability_1_returns_what_we_knew_then_for_the_valid_time()
    {
        var streamId = await SeedCorrectedHistoryAsync();

        // "What was the principal on 2026-04-01, as we knew it on 2026-04-01?" — the wrong value,
        // which is what we knew then (event-store §6.4 worked example).
        var asKnownThen = await _query.AsOfAsync(
            streamId, Kind, validTime: new DateTimeOffset(2026, 4, 1, 0, 0, 0, TimeSpan.Zero), knownAt: new DateTimeOffset(2026, 4, 1, 0, 0, 0, TimeSpan.Zero));

        Assert.NotNull(asKnownThen);
        Assert.Equal(WrongPrincipal, asKnownThen.State.PrincipalCents);
        // The row carries its eventual supersession stamp (it WAS later corrected), but its
        // half-open belief interval [recorded_at, superseded_at) covered the 2026-04-01 knownAt
        // lens — that is why as-of-then returns it. The stamp is the row's fate, not the lens.
        Assert.Equal(CorrectedAt, asKnownThen.SupersededAt);

        // "...as we know it now (post-correction)?" — the corrected value, projected back over the
        // same valid-time. as-of and current-belief genuinely differ — the bitemporal commitment.
        var asKnownNow = await _query.AsOfAsync(
            streamId, Kind, validTime: new DateTimeOffset(2026, 4, 1, 0, 0, 0, TimeSpan.Zero), knownAt: CorrectedAt.AddDays(1));

        Assert.NotNull(asKnownNow);
        Assert.Equal(TruePrincipal, asKnownNow.State.PrincipalCents);
        Assert.NotEqual(asKnownThen.State.PrincipalCents, asKnownNow.State.PrincipalCents);
    }

    [Fact]
    public async Task AsOf_is_null_before_the_position_existed_in_belief_time()
    {
        var streamId = await SeedCorrectedHistoryAsync();

        // Before the first belief was recorded there is nothing to know.
        var before = await _query.AsOfAsync(
            streamId, Kind, validTime: ValidFrom, knownAt: RecordedThen.AddMinutes(-1));

        Assert.Null(before);
    }

    // ---------- #2 Audit trail (HistoryOf): how belief about the position changed ----------

    [Fact]
    public async Task HistoryOf_capability_2_returns_the_full_belief_line_in_belief_time_order()
    {
        var streamId = await SeedCorrectedHistoryAsync();

        var history = await _query.HistoryOfAsync(streamId, Kind);

        // Both beliefs are present — the disavowed original and the current correction — and ordered
        // by belief-time, so the audit trail reads as "we believed €10,000, then corrected to €100,000".
        Assert.Equal(2, history.Count);

        Assert.Equal(WrongPrincipal, history[0].State.PrincipalCents);
        Assert.Equal(RecordedThen, history[0].RecordedAt);
        Assert.Equal(CorrectedAt, history[0].SupersededAt); // the original was superseded at correction time

        Assert.Equal(TruePrincipal, history[1].State.PrincipalCents);
        Assert.Equal(CorrectedAt, history[1].RecordedAt);
        Assert.Null(history[1].SupersededAt); // the current belief sorts last
    }

    [Fact]
    public async Task HistoryOf_is_empty_when_the_pair_was_never_projected()
    {
        await ResetAsync();
        Assert.Empty(await _query.HistoryOfAsync(Guid.NewGuid(), Kind));
    }

    // ---------- #3 Counterfactual replay: the disavowed vs corrected belief over the same valid-time ----------

    [Fact]
    public async Task Counterfactual_capability_3_reads_disavowed_and_corrected_beliefs_for_the_same_valid_time()
    {
        var streamId = await SeedCorrectedHistoryAsync();
        var validTime = new DateTimeOffset(2026, 4, 1, 0, 0, 0, TimeSpan.Zero);

        // The disavowed belief: what the projection asserted about 2026-04-01 BEFORE the correction
        // landed — read by pinning knownAt to just before the correction's transaction_time.
        var disavowed = await _query.AsOfAsync(streamId, Kind, validTime, knownAt: CorrectedAt.AddTicks(-1));
        // The corrected belief: the same valid-time as we know it now.
        var corrected = await _query.AsOfAsync(streamId, Kind, validTime, knownAt: CorrectedAt);

        Assert.NotNull(disavowed);
        Assert.NotNull(corrected);
        Assert.Equal(WrongPrincipal, disavowed.State.PrincipalCents);
        Assert.Equal(TruePrincipal, corrected.State.PrincipalCents);
        // The half-open belief interval [recorded_at, superseded_at): exactly at the correction's
        // transaction_time the new belief is live and the old one is gone.
        Assert.NotEqual(disavowed.State.PrincipalCents, corrected.State.PrincipalCents);
    }

    // ---------- #4 Forward projection: a future valid-time under the current belief ----------

    [Fact]
    public async Task ForwardProjection_capability_4_reads_a_future_valid_time_under_the_current_belief()
    {
        var streamId = await SeedCorrectedHistoryAsync();

        // "What will the principal be on 2027-03-15 if no further events occur?" — the open-ended
        // current belief (valid_to NULL) covers every future valid-time, as known now.
        var future = new DateTimeOffset(2027, 3, 15, 0, 0, 0, TimeSpan.Zero);
        var forward = await _query.AsOfAsync(streamId, Kind, validTime: future, knownAt: CorrectedAt.AddYears(1));

        Assert.NotNull(forward);
        Assert.Equal(TruePrincipal, forward.State.PrincipalCents);
        Assert.Null(forward.ValidTo); // open-ended world-time slice — current and onward

        // CurrentBelief agrees: the forward projection of a current-and-onward position is the
        // current belief.
        var current = await _query.CurrentBeliefAsync(streamId, Kind);
        Assert.NotNull(current);
        Assert.Equal(forward.State.PrincipalCents, current.State.PrincipalCents);
        Assert.Equal(1, current.SourceSequence);
    }

    // --- helpers ---

    private static ProjectionRecord Record(Guid streamId, long principalCents, DateTimeOffset recordedAt, long sourceSequence) =>
        new(
            StreamId: streamId,
            ProjectionKind: Kind,
            SourceSequence: sourceSequence,
            ValidFrom: ValidFrom,
            ValidTo: null, // open-ended: the position holds from constitution onward
            RecordedAt: recordedAt,
            SupersededAt: null,
            StructuralPayload: JsonSerializer.SerializeToUtf8Bytes(new DepositPosition(principalCents)),
            PiiCiphertext: ReadOnlyMemory<byte>.Empty);

    /// <summary>A minimal structural projection state — principal only — for the bitemporal history.</summary>
    private sealed record DepositPosition(long PrincipalCents);
}
