using System.Diagnostics.CodeAnalysis;

namespace Babelstone.LoadHarness;

/// <summary>
/// The harness's controllable clock seam (ADR-PC-011 §G3 / §P3). The engine accepts an injected
/// <see cref="TimeProvider"/> for determinism (ADR-PC-010 §P5); the harness drives the SAME seam so
/// a run reproduces from <c>(seed, code revision)</c> and so month-end lifecycle events fire at
/// <i>simulated</i> month-end rather than wall-clock month-end.
/// </summary>
/// <remarks>
/// <para>
/// In plain English: this is a clock the test owns. Real time keeps ticking on the wall, but the
/// engine asks <i>this</i> object "what time is it?" — so the test can fast-forward a whole simulated
/// month into a few seconds of real time and watch the engine fire its month-end events on cue.
/// </para>
/// <para>
/// Per §P3 the clock governs <i>domain</i> time, NOT the producer's emission rate: throughput
/// (250 TPS sustained, 1000 TPS burst) still runs against real wall-clock rate. <see cref="Advance"/>
/// moves simulated time forward; the producer's pacing is a separate concern owned by the driver.
/// </para>
/// <para>
/// The advance is a deliberate single-threaded operation: the harness advances the clock between
/// workload phases (e.g. across a simulated day boundary), not concurrently with event emission, so
/// no locking is needed and the simulated-now read stays a plain volatile field read.
/// </para>
/// </remarks>
public sealed class SimulatedClock : TimeProvider
{
    private long _utcTicks;

    /// <summary>Creates a clock anchored at <paramref name="start"/> (the simulated window's t0).</summary>
    public SimulatedClock(DateTimeOffset start)
    {
        _utcTicks = start.UtcDateTime.Ticks;
    }

    /// <summary>The current SIMULATED instant (UTC). This is what the engine reads as "now".</summary>
    public override DateTimeOffset GetUtcNow() =>
        new(Interlocked.Read(ref _utcTicks), TimeSpan.Zero);

    /// <summary>
    /// Moves simulated time forward by <paramref name="delta"/>. The next engine append sees the new
    /// instant; advancing past a simulated day/month boundary is what makes the engine emit
    /// <c>DailyAccrualClosed</c> / <c>StatementCycleClosed</c> / <c>FeeAssessed</c> itself (§P3) —
    /// the harness never fakes those events directly (ADR-PC-011 §8.4 "not via internal entry points").
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">If <paramref name="delta"/> is negative — simulated time is monotonic.</exception>
    public void Advance(TimeSpan delta)
    {
        if (delta < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(delta), delta, "Simulated time is monotonic; it cannot move backwards.");
        }

        Interlocked.Add(ref _utcTicks, delta.Ticks);
    }

    /// <summary>Sets simulated time to an absolute instant (used to jump to a known calendar boundary).</summary>
    /// <exception cref="ArgumentOutOfRangeException">If <paramref name="instant"/> precedes the current simulated now.</exception>
    public void AdvanceTo(DateTimeOffset instant)
    {
        var targetTicks = instant.UtcDateTime.Ticks;
        var currentTicks = Interlocked.Read(ref _utcTicks);
        if (targetTicks < currentTicks)
        {
            throw new ArgumentOutOfRangeException(
                nameof(instant), instant, "Simulated time is monotonic; it cannot move backwards.");
        }

        Interlocked.Exchange(ref _utcTicks, targetTicks);
    }

    // The harness paces the producer against real wall-clock (the throughput dimension, §P3), so the
    // high-frequency timestamp and timer surface stays the system one — only domain "now" is simulated.
    [ExcludeFromCodeCoverage(Justification = "Delegates to the wall-clock base for the throughput dimension (§P3).")]
    public override long GetTimestamp() => System.GetTimestamp();

    [ExcludeFromCodeCoverage(Justification = "Delegates to the wall-clock base for the throughput dimension (§P3).")]
    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
        System.CreateTimer(callback, state, dueTime, period);
}
