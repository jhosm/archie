using Babelstone.Engine;
using Babelstone.EventStore;
using Babelstone.FinancialTypes;
using Babelstone.RateSheets;
using Npgsql;
using Xunit;

namespace Babelstone.Families.TermDeposit.Application.Tests;

/// <summary>
/// D.5 forced-correction round-trip ACCEPTANCE test (ADR-PC-002 §P2 — corrections supersede, never
/// overwrite; spike criterion #1), end-to-end on the REAL term-deposit family against real
/// PostgreSQL (Testcontainers). It proves the bitemporal contract the whole Path-A decision rests
/// on (event-store §6, §7.1): after a retroactive <c>DepositCorrected</c>, BOTH "what we knew then"
/// and "what we now know" stay queryable, the supersession is visible in belief-history, and the
/// prior belief is never deleted or overwritten.
/// </summary>
/// <remarks>
/// The lifecycle (constitute→accrue→withhold→mature) runs through the real
/// <see cref="TermDepositConstitutionService"/>; the correction is appended directly through the
/// <see cref="AggregateRuntime{DepositPosition}"/> because the service has no correction command yet
/// (the read-model correction is exactly this D.5 work, not a family-source change). A deterministic
/// clock stamps each append's transaction_time, so the correction's belief-time is strictly after
/// the lifecycle's — which is what makes <c>AsOf(validTime, knownAt = before-the-correction)</c>
/// return the disavowed belief and <c>CurrentBelief()</c> the corrected one.
/// </remarks>
[Trait("Category", "Integration")]
public sealed class ForcedCorrectionRoundTripTests(ConstitutionFixture fixture)
    : IClassFixture<ConstitutionFixture>
{
    private const string Kind = TermDepositProjectionModule.DepositPositionKind;

    [Fact]
    public async Task DepositCorrected_supersedes_the_prior_belief_and_keeps_both_queryable()
    {
        await fixture.EnsureRateSheetAsync(SharedSheet);
        await ResetProjectionsAsync();

        // A controllable clock: the runtime stamps transaction_time from it. Each append advances it,
        // so the correction (the last append) carries a strictly-later belief-time than the lifecycle.
        var clock = new SteppingClock(new DateTimeOffset(2026, 1, 15, 9, 0, 0, TimeSpan.Zero), TimeSpan.FromHours(1));
        var store = new PostgresEventStore(fixture.ConnectionString);
        var runtime = new AggregateRuntime<DepositPosition>(
            store, new EventStoreSink(store), TermDepositFamilyModule.Registry(),
            new JsonEventSerializer(), new NullPiiProtector(), clock, () => DepositPosition.Empty);
        var service = new TermDepositConstitutionService(
            runtime, new PostgresRateSheetStore(fixture.ConnectionString),
            SkeletonPack.LoadPt2026(), dayCountPrimitive: "act_360", withholdingPrimitive: "irs_juros");

        var depositId = Guid.NewGuid();
        await service.ConstituteAsync(new ConstituteDepositCommand(
            DepositId: depositId, PrincipalCents: 1_000_000, ProductId: "dpz_pt_12m_juros_venc", Role: "standard",
            TermDays: 365, StartDate: new DateOnly(2026, 1, 15),
            ConstitutedAt: new DateTimeOffset(2026, 1, 15, 0, 0, 0, TimeSpan.Zero),
            InterestVariant: "AT_MATURITY", AutoRenewalPolicy: "NONE", FundingAccount: "PT50-DDA-001", Actor: "mcp:dev"));
        await service.MatureAsync(new MatureDepositCommand(
            DepositId: depositId, MaturedAt: new DateTimeOffset(2027, 1, 15, 0, 0, 0, TimeSpan.Zero),
            PayoutAccount: "PT50-DDA-001", Actor: "mcp:dev"));

        // Drain the bitemporal deposit-position projection so it holds the pre-correction belief.
        var (drainer, runner, query) = BuildProjection(store, clock);
        await drainer.DrainOnceAsync(runner);
        var beforeBelief = await query.CurrentBeliefAsync(depositId, Kind);
        Assert.NotNull(beforeBelief);
        Assert.Equal(0, beforeBelief.State.CorrectionCount);          // no correction folded yet
        var correctionKnownFloor = beforeBelief.RecordedAt;          // belief-time of the maturity append

        // A retroactive clerk-data-entry correction (event-store §4.2 / §6.4 worked example). The
        // fold tallies CorrectionCount; the supersede-then-insert is the projection runtime's job.
        var head = (await runtime.LoadAsync(depositId)).Version;
        var corrected = new DepositCorrected(
            DepositId: depositId, CorrectionId: "corr-001", CorrectedField: "principal",
            PreviousValueRef: "ref:old", CorrectedValueRef: "ref:new",
            EffectiveFrom: new DateOnly(2026, 1, 15), CorrectionReason: "clerk-entry");
        await runtime.AppendAsync(
            depositId, head, [corrected],
            new AppendContext("term_deposit", "pt.2026.1", "term_deposit@2026.1", "ops:clerk",
                ValidTime: new DateTimeOffset(2026, 1, 15, 0, 0, 0, TimeSpan.Zero)));

        await drainer.DrainOnceAsync(runner);

        // 1. CurrentBelief is the CORRECTED belief — CorrectionCount advanced to 1.
        var corrupted = await query.CurrentBeliefAsync(depositId, Kind);
        Assert.NotNull(corrupted);
        Assert.Equal(1, corrupted.State.CorrectionCount);

        // 2. AsOf the prior valid-time, KNOWN AT just before the correction landed, returns the
        //    DISAVOWED belief — what we knew then (CorrectionCount still 0). This is the §P2 round-trip.
        var disavowed = await query.AsOfAsync(
            depositId, Kind, validTime: new DateTimeOffset(2027, 1, 15, 0, 0, 0, TimeSpan.Zero),
            knownAt: correctionKnownFloor);
        Assert.NotNull(disavowed);
        Assert.Equal(0, disavowed.State.CorrectionCount);

        // 3. History shows SUPERSESSION, never overwrite: the prior belief is still present with a
        //    non-null superseded_at, and exactly one current belief (superseded_at IS NULL) remains.
        var history = await query.HistoryOfAsync(depositId, Kind);
        Assert.True(history.Count >= 2, "the correction must add a belief row, not overwrite the prior one");
        Assert.Equal(1, history.Count(r => r.SupersededAt is null));     // exactly one current belief
        Assert.Contains(history, r => r.SupersededAt is not null);       // disavowed beliefs preserved
        // The belief the correction directly disavowed is the matured leg (CorrectionCount 0); the
        // current belief is the corrected leg (CorrectionCount 1).
        Assert.Equal(0, history.Where(r => r.SupersededAt is not null).Max(r => r.State.CorrectionCount));
        Assert.Equal(1, history.Single(r => r.SupersededAt is null).State.CorrectionCount);

        // 4. The byte store NEVER deletes: every prior belief the fold superseded physically remains
        //    on the table (the one-per-event supersession chain plus the correction's), so the full
        //    belief history is auditable — supersede, never overwrite (ADR-PC-002 §P2).
        Assert.True(await CountSupersededAsync(depositId) >= 1, "superseded beliefs must be preserved, not deleted");
    }

    // --- composition helpers ---

    private (ProjectionDrainer Drainer, IProjectionRunner Runner, BitemporalProjectionQuery<DepositPosition> Query)
        BuildProjection(PostgresEventStore store, TimeProvider clock)
    {
        var storage = new PostgresProjectionStore(fixture.ConnectionString);
        var infra = new ProjectionInfra(storage, new JsonEventSerializer());
        // Select the deposit-position runner BY KIND: F.6 grew the module to four runners, so a
        // bare Single() (the original D.5 composition) now throws — this test only drains the
        // bitemporal deposit-position projection.
        var runner = new TermDepositProjectionModule().CreateRunners(infra).Single(r => r.Kind == Kind);
        var checkpoints = new PostgresProjectionCheckpointStore(fixture.ConnectionString);
        var drainer = new ProjectionDrainer(store, checkpoints, clock);
        var query = new BitemporalProjectionQuery<DepositPosition>(storage, new JsonStateSerializer<DepositPosition>());
        return (drainer, runner, query);
    }

    private async Task ResetProjectionsAsync()
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "TRUNCATE projections; TRUNCATE projection_checkpoints;", connection);
        await command.ExecuteNonQueryAsync();
    }

    private async Task<int> CountSupersededAsync(Guid streamId)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT count(*) FROM projections WHERE stream_id = @s AND projection_kind = @k AND superseded_at IS NOT NULL;",
            connection);
        command.Parameters.AddWithValue("s", streamId);
        command.Parameters.AddWithValue("k", Kind);
        return (int)(long)(await command.ExecuteScalarAsync())!;
    }

    private static RateSheet SharedSheet => TestRateSheets.MultiPriced(
        versionId: "pt-deposits-2026.1",
        effectiveFrom: new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero),
        ("dpz_pt_12m_juros_venc", "standard", 300),
        ("dpz_pt_12m_juros_mensal", "standard", 325),
        ("dpz_pt_12m_juros_antecip", "standard", 300));

    /// <summary>
    /// A deterministic <see cref="TimeProvider"/> whose <see cref="GetUtcNow"/> advances by a fixed
    /// step each call — so successive appends carry strictly-increasing transaction_time and the
    /// correction's belief-time is provably after the lifecycle's. The engine (not the handler) reads
    /// the clock (ADR-PC-010 §P5); this only makes the runtime's clock reads reproducible.
    /// </summary>
    private sealed class SteppingClock(DateTimeOffset start, TimeSpan step) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public override DateTimeOffset GetUtcNow()
        {
            var current = _now;
            _now = _now.Add(step);
            return current;
        }
    }
}
