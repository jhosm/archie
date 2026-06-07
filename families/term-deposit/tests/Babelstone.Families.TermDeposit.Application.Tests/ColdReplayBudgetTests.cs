using System.Diagnostics;
using Babelstone.Engine;
using Babelstone.EventStore;
using Babelstone.FinancialTypes;
using Xunit;
using Xunit.Abstractions;

namespace Babelstone.Families.TermDeposit.Application.Tests;

/// <summary>
/// D.5 cold-replay budget test for the <c>REPLAY_BUDGET_5S_30S</c> commitment (event-store §8.2):
/// the v1 (with-a-plan) half — cold replay of ONE instance's full lifecycle (~24-260 events for a
/// term deposit) rebuilds its state from the first event in UNDER 5 SECONDS, with NO snapshots
/// (snapshots are an optimisation, not the correctness path — §8). The v4 (irregular,
/// ~250-1000-event, 30 s) half is L.3's load-harness concern and is NOT flipped here.
/// </summary>
/// <remarks>
/// <para>
/// "Cold replay" is exactly <see cref="AggregateRuntime{DepositPosition}.LoadAsync"/> with no
/// snapshot store wired: it folds the whole stream from <c>sequence_number</c> 0. The corpus is a
/// realistic E.3-shaped lifecycle — constitute → accrue×N → mature — built directly through the
/// runtime against real PostgreSQL (Testcontainers). A hand-built corpus is fine for D.5; F.8's
/// sealed corpus comes later. The deposit-position fold accumulates each <c>InterestAccrued</c>, so
/// an N-accrual instance is a genuine N+2-event replay, not a degenerate one.
/// </para>
/// <para>
/// CI-variance robustness: the assertion uses the real §8.2 budget (5 s) rather than a tightened
/// margin, and the per-event work (one row read + JSON decode + a pure <c>state with</c> fold) is
/// trivial, so a 262-event replay lands two orders of magnitude inside budget even on a loaded
/// shared CI runner — the measured time is logged. The budget can be overridden for an unusually
/// slow environment via <c>BABELSTONE_REPLAY_BUDGET_MS</c> (documented escape hatch); it is never
/// tightened below the spec, only relaxed.
/// </para>
/// </remarks>
[Trait("Category", "Integration")]
public sealed class ColdReplayBudgetTests(ConstitutionFixture fixture, ITestOutputHelper output)
    : IClassFixture<ConstitutionFixture>
{
    // The §8.2 v1 budget. Overridable upward only (CI escape hatch); never tightened below spec.
    private static readonly TimeSpan V1Budget = ResolveBudget(TimeSpan.FromSeconds(5));

    [Fact]
    public async Task REPLAY_BUDGET_5S_30S_v1_cold_replay_of_one_instance_is_under_5s()
    {
        // 260 accruals → 262 events (constitute + 260 × InterestAccrued + mature): the TOP of the
        // §8.2 v1 with-a-plan range (~24-260), the worst v1 case, so passing here passes the range.
        const int accruals = 260;
        var store = new PostgresEventStore(fixture.ConnectionString);
        var depositId = await SeedLifecycleAsync(store, accruals);

        // Cold replay: a FRESH runtime with NO snapshot store folds the stream from sequence 0.
        var coldRuntime = new AggregateRuntime<DepositPosition>(
            store, new EventStoreSink(store), TermDepositFamilyModule.Registry(),
            new JsonEventSerializer(), new NullPiiProtector(), TimeProvider.System,
            () => DepositPosition.Empty);

        var sw = Stopwatch.StartNew();
        var hydrated = await coldRuntime.LoadAsync(depositId);
        sw.Stop();

        output.WriteLine(
            $"REPLAY_BUDGET_5S_30S v1: cold-replayed {hydrated.Version + 1} events in {sw.Elapsed.TotalMilliseconds:F0} ms " +
            $"(budget {V1Budget.TotalMilliseconds:F0} ms).");

        // Correctness FIRST: a cold replay that is fast but wrong proves nothing. 260 accruals folded.
        Assert.Equal(accruals + 1, hydrated.Version);                 // sequences 0..261 → head 261
        Assert.Equal(DepositLifecycle.Matured, hydrated.State.Lifecycle);
        Assert.Equal(new Money(accruals * AccrualCents), hydrated.State.AccruedGrossInterest);

        Assert.True(
            sw.Elapsed < V1Budget,
            $"cold replay of {hydrated.Version + 1} events took {sw.Elapsed.TotalMilliseconds:F0} ms, " +
            $"over the §8.2 v1 budget of {V1Budget.TotalMilliseconds:F0} ms.");
    }

    private const long AccrualCents = 100; // each synthetic accrual adds €1.00 gross — keeps the fold real.

    /// <summary>
    /// Builds a constitute → accrue×N → mature lifecycle on one stream directly through the runtime
    /// (no decider/rate-sheet round-trips — this is a replay benchmark, not a pricing test), so the
    /// stream carries N+2 real events the cold fold accumulates. Events are appended in batches at the
    /// live head with optimistic concurrency, exactly as the runtime commits them in production.
    /// </summary>
    private static async Task<Guid> SeedLifecycleAsync(PostgresEventStore store, int accruals)
    {
        var runtime = new AggregateRuntime<DepositPosition>(
            store, new EventStoreSink(store), TermDepositFamilyModule.Registry(),
            new JsonEventSerializer(), new NullPiiProtector(), TimeProvider.System,
            () => DepositPosition.Empty);
        var context = new AppendContext(
            "term_deposit", "pt.2026.1", "term_deposit@2026.1", "bench",
            ValidTime: new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));

        var depositId = Guid.NewGuid();
        var constituted = new DepositConstituted(
            DepositId: depositId, Principal: new Money(1_000_000), TanBasisPoints: 300,
            RateSheetVersionId: "pt-deposits-2026.1", TermDays: 365,
            StartDate: new DateOnly(2026, 1, 1), MaturityDate: new DateOnly(2027, 1, 1),
            InterestVariant: "AT_MATURITY", AutoRenewalPolicy: "NONE");
        await runtime.AppendAsync(depositId, expectedVersion: -1, [constituted], context);
        var head = 0L;

        // Append the accruals in chunks to keep transactions a reasonable size, advancing the head.
        const int chunk = 50;
        for (var done = 0; done < accruals; done += chunk)
        {
            var batch = new List<DomainEvent>();
            for (var i = 0; i < chunk && done + i < accruals; i++)
            {
                batch.Add(new InterestAccrued(new Money(AccrualCents), new DateOnly(2026, 1, 1).AddDays(done + i + 1)));
            }

            await runtime.AppendAsync(depositId, head, batch, context);
            head += batch.Count;
        }

        // Close the lifecycle: a single DepositMatured terminator.
        var totalGross = accruals * AccrualCents;
        await runtime.AppendAsync(
            depositId, head,
            [new DepositMatured(new Money(1_000_000), new Money(totalGross), new Money(1_000_000 + totalGross), new DateOnly(2027, 1, 1))],
            context);

        return depositId;
    }

    private static TimeSpan ResolveBudget(TimeSpan spec)
    {
        var overrideMs = Environment.GetEnvironmentVariable("BABELSTONE_REPLAY_BUDGET_MS");
        if (overrideMs is not null && long.TryParse(overrideMs, out var ms) && ms > spec.TotalMilliseconds)
        {
            return TimeSpan.FromMilliseconds(ms);
        }

        return spec;
    }
}
