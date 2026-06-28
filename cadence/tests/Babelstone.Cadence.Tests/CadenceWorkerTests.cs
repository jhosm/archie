using Babelstone.Cadence;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Babelstone.Cadence.Tests;

/// <summary>
/// Tests for <see cref="CadenceWorker"/> — the generic clock-owning poll-loop worker (ADR-PC-036 §Decision 2 +
/// ADR-IC-019 mechanism reuse). They cover the three load-bearing loop behaviours the notification scheduler
/// and the lifecycle-command driver both rely on:
/// <list type="bullet">
/// <item>the worker OWNS the clock — it derives the as-of date from the injected <see cref="TimeProvider"/> and
/// hands it to the pass, which never reads the clock itself (ADR-PC-023 §6);</item>
/// <item>a clean tick repeats on the configured cadence (the pass runs again, and again);</item>
/// <item>a pass-cycle exception is BACKPRESSURE, not fatal — the worker backs off and retries, then runs a
/// clean pass.</item>
/// </list>
/// The loop is driven by a hand-rolled fake <see cref="TimeProvider"/> (fixed <c>GetUtcNow</c>, real timers) so
/// no <c>FakeTimeProvider</c> package is needed; a tiny poll interval keeps the loop fast, and each test waits on
/// a pass-signalled completion (never a fixed sleep) so it is deterministic.
/// </summary>
public sealed class CadenceWorkerTests
{
    private static readonly TimeSpan Tick = TimeSpan.FromMilliseconds(1);
    private static readonly TimeSpan SafetyTimeout = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task The_worker_derives_the_as_of_date_from_the_clock_and_hands_it_to_the_pass()
    {
        var clock = new FixedClock(new DateTimeOffset(2026, 6, 24, 9, 30, 0, TimeSpan.Zero));
        var expected = new DateOnly(2026, 6, 24);
        var pass = new CountingPass(target: 1);
        var worker = NewWorker(pass, clock);

        await StartAsync(worker);
        await pass.Reached.WaitAsync(SafetyTimeout);
        await StopAsync(worker);

        Assert.NotEmpty(pass.AsOfs);
        // The clock lives in the worker, never in the pass (ADR-PC-023 §6): every tick saw the SAME fixed date.
        Assert.All(pass.AsOfs, asOf => Assert.Equal(expected, asOf));
    }

    [Fact]
    public async Task A_clean_tick_repeats_on_the_cadence()
    {
        var pass = new CountingPass(target: 3);
        var worker = NewWorker(pass, new FixedClock(DateTimeOffset.UtcNow));

        await StartAsync(worker);
        await pass.Reached.WaitAsync(SafetyTimeout);
        await StopAsync(worker);

        // The loop ran the pass more than once — the cadence, not a single shot.
        Assert.True(pass.Count >= 3, $"expected the pass to run at least 3 times, ran {pass.Count}");
        Assert.True(worker.ExecuteTask!.IsCompletedSuccessfully, "the worker stopped gracefully");
    }

    [Fact]
    public async Task A_pass_exception_is_backpressure_and_the_worker_retries_to_a_clean_pass()
    {
        // Throw on the first attempt, then succeed: the worker must treat the throw as backpressure (back off
        // and retry), not as a fatal error that kills the loop.
        var pass = new FlakyPass(throwUntilAttempt: 1);
        var worker = NewWorker(pass, new FixedClock(DateTimeOffset.UtcNow));

        await StartAsync(worker);
        await pass.FirstSuccess.WaitAsync(SafetyTimeout);
        await StopAsync(worker);

        Assert.True(pass.Attempts >= 2, $"expected a retry after the throw, saw {pass.Attempts} attempt(s)");
        Assert.True(pass.Successes >= 1, "the worker reached a clean pass after backing off");
        Assert.True(worker.ExecuteTask!.IsCompletedSuccessfully, "the worker stopped gracefully");
    }

    // --- helpers ---

    private static CadenceWorker NewWorker(ISchedulePass pass, TimeProvider clock) =>
        new(pass, new CadenceSchedulerOptions { PollInterval = Tick }, clock, NullLogger<CadenceWorker>.Instance);

    private static Task StartAsync(CadenceWorker worker) =>
        ((IHostedService)worker).StartAsync(CancellationToken.None);

    private static Task StopAsync(CadenceWorker worker) =>
        ((IHostedService)worker).StopAsync(CancellationToken.None);

    /// <summary>A <see cref="TimeProvider"/> with a fixed wall clock but real timers — overriding only
    /// <see cref="TimeProvider.GetUtcNow"/> leaves the default (system) timer in place, so the loop's
    /// <c>Task.Delay</c> ticks for real while the as-of date the worker derives stays pinned.</summary>
    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    /// <summary>An <see cref="ISchedulePass"/> that records every as-of date it is handed and signals when it
    /// has run <c>target</c> times.</summary>
    private sealed class CountingPass(int target) : ISchedulePass
    {
        private readonly Lock _gate = new();
        private readonly List<DateOnly> _asOfs = [];
        private readonly TaskCompletionSource _reached = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Reached => _reached.Task;

        public int Count
        {
            get { lock (_gate) { return _asOfs.Count; } }
        }

        public IReadOnlyList<DateOnly> AsOfs
        {
            get { lock (_gate) { return _asOfs.ToList(); } }
        }

        public Task RunOnceAsync(DateOnly asOf, CancellationToken ct = default)
        {
            lock (_gate)
            {
                _asOfs.Add(asOf);
                if (_asOfs.Count >= target)
                {
                    _reached.TrySetResult();
                }
            }

            return Task.CompletedTask;
        }
    }

    /// <summary>An <see cref="ISchedulePass"/> that throws on its first <c>throwUntilAttempt</c> invocation(s)
    /// then succeeds — to prove the worker treats a throw as backpressure and retries to a clean pass.</summary>
    private sealed class FlakyPass(int throwUntilAttempt) : ISchedulePass
    {
        private readonly Lock _gate = new();
        private readonly TaskCompletionSource _firstSuccess = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task FirstSuccess => _firstSuccess.Task;
        public int Attempts { get; private set; }
        public int Successes { get; private set; }

        public Task RunOnceAsync(DateOnly asOf, CancellationToken ct = default)
        {
            lock (_gate)
            {
                Attempts++;
                if (Attempts <= throwUntilAttempt)
                {
                    throw new InvalidOperationException("simulated downstream backpressure");
                }

                Successes++;
                _firstSuccess.TrySetResult();
            }

            return Task.CompletedTask;
        }
    }
}
