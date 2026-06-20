using Babelstone.LoadHarness;
using Xunit;

namespace Babelstone.LoadHarness.Runner.Tests;

/// <summary>
/// Testcontainers integration coverage for <see cref="EngineProjectionRig"/> — the host's live-stack
/// measured path (ADR-PC-011 §G2). It drives the rig's real append / drain / cold-replay / no-divergence
/// members against a Testcontainers PostgreSQL event store (bd babelstone-2e6q.7), replacing the
/// <c>[ExcludeFromCodeCoverage]</c> the rig previously carried with measured branches.
/// </summary>
/// <remarks>
/// In plain English: the load-harness runner drives the engine's real save-and-rebuild code against a
/// real database to measure how long commits and rebuilds take. That code used to have no automated test
/// (only a hand-run live stack), so its lines were excluded from the coverage count. These tests stand up
/// a throwaway PostgreSQL in Docker and exercise every one of those members, so they are now measured.
///
/// Carries <c>[Trait("Category", "Integration")]</c> so the Docker-free unit lane skips it; the CI
/// integration lane (<c>--filter "Category=Integration"</c>) runs it.
/// </remarks>
[Trait("Category", "Integration")]
[Collection("RunnerPostgres")]
public sealed class EngineProjectionRigIntegrationTests
{
    private readonly RunnerPostgresFixture _pg;

    public EngineProjectionRigIntegrationTests(RunnerPostgresFixture pg) => _pg = pg;

    // A short, deterministic batch of synthetic events for a single run (the harness-emitted classes only).
    private static IReadOnlyList<SyntheticEvent> SeedEvents(int seed, int count)
        => new WorkloadGenerator(seed, WorkloadSpec.Default(), Calibration.V4Placeholder())
            .Generate(count, new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), TimeSpan.FromHours(24), new DateOnly(2026, 11, 27))
            .ToList();

    private EngineProjectionRig NewRig(Guid runNonce)
        => new(_pg.ConnectionString, TimeProvider.System, runNonce);

    [Fact]
    public async Task AppendWithSpan_opens_a_stream_and_returns_the_new_head()
    {
        var rig = NewRig(Guid.NewGuid());

        // Each synthetic constitution opens a NEW stream keyed by its run-namespaced deposit id, so the
        // first append on each stream returns head sequence 0 (expectedVersion -1, no contention).
        var head = await rig.AppendWithSpanAsync(SeedEvents(seed: 11, count: 1)[0]);

        Assert.Equal(0, head);
        Assert.Single(rig.AppendedStreams);
    }

    [Fact]
    public async Task Drain_folds_every_appended_event_into_the_projection()
    {
        var rig = NewRig(Guid.NewGuid());
        var events = SeedEvents(seed: 22, count: 8);
        foreach (var synthetic in events)
        {
            await rig.AppendWithSpanAsync(synthetic);
        }

        // The async projector catches up over everything appended so far; one event per stream is folded.
        var folded = await rig.DrainAsync();

        Assert.Equal(events.Count, folded);
        Assert.Equal(events.Count, rig.AppendedStreams.Count);
    }

    [Fact]
    public async Task TimeColdReplay_refolds_the_stream_from_the_log_within_a_finite_budget()
    {
        var rig = NewRig(Guid.NewGuid());
        await rig.AppendWithSpanAsync(SeedEvents(seed: 33, count: 1)[0]);
        await rig.DrainAsync();

        var (elapsedMs, refolded) = await rig.TimeColdReplayAsync(rig.AppendedStreams[0]);

        // The cold fold re-reads the stream from sequence 0 (one constitution event here) and the timer
        // is a real, finite wall-clock measurement — well inside even the with-a-plan 5s budget.
        Assert.Equal(1, refolded);
        Assert.True(elapsedMs >= 0.0, $"elapsed {elapsedMs}ms should be a real measurement");
        Assert.True(elapsedMs < 5_000.0, $"a single-event cold replay should be well under the 5s budget (was {elapsedMs}ms)");
    }

    [Fact]
    public async Task NoDivergenceDrill_finds_zero_divergent_streams_after_a_clean_rebuild()
    {
        var rig = NewRig(Guid.NewGuid());
        var events = SeedEvents(seed: 44, count: 6);
        foreach (var synthetic in events)
        {
            await rig.AppendWithSpanAsync(synthetic);
        }

        await rig.DrainAsync();

        var (checked_, divergent, refolded) = await rig.RunNoDivergenceDrillAsync();

        // A clean rebuild (supersede-all + checkpoint reset + cold re-fold) must reproduce every stream's
        // running belief byte-for-byte: the §8.3 no-rebuild-divergence invariant. The drill reads ALL
        // family streams, so it is >= this run's appends; none may diverge.
        Assert.True(checked_ >= events.Count, $"the drill should cover at least this run's {events.Count} streams (saw {checked_})");
        Assert.Equal(0, divergent);
        Assert.True(refolded >= events.Count, $"the rebuild should re-fold at least this run's {events.Count} events (saw {refolded})");
    }

    [Fact]
    public async Task A_repeated_run_with_a_fresh_nonce_does_not_collide_on_stream_ids()
    {
        // The same (seed) reproduces the same synthetic deposit ids; a fresh run nonce namespaces them so
        // appending the SAME seeded batch twice never hits the optimistic-concurrency head (§8.5).
        var batch = SeedEvents(seed: 55, count: 3);

        var first = NewRig(Guid.NewGuid());
        var second = NewRig(Guid.NewGuid());
        foreach (var synthetic in batch)
        {
            await first.AppendWithSpanAsync(synthetic);
            await second.AppendWithSpanAsync(synthetic);
        }

        // Two nonces → two disjoint stream-id spaces for the same seeded keys (no ConcurrencyException).
        Assert.Equal(batch.Count, first.AppendedStreams.Count);
        Assert.Equal(batch.Count, second.AppendedStreams.Count);
        Assert.Empty(first.AppendedStreams.Intersect(second.AppendedStreams));
    }
}
