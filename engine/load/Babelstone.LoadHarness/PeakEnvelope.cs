namespace Babelstone.LoadHarness;

/// <summary>
/// The §8.2 peak structure as a deterministic rate-multiplier over a simulated instant. Given a
/// simulated time, it returns the instantaneous TPS multiplier over the sustained average from the
/// composed daily / monthly / annual peak shapes (ADR-PC-011 §8.2):
/// <list type="bullet">
///   <item>Daily — lunch (12:00–14:00) and after-work (18:00–21:00) Lisbon drive the daily multiplier.</item>
///   <item>Monthly — payday morning (the 1st and 25th) concentrates the monthly multiplier for ~15 min.</item>
///   <item>Annual — one synthetic annual-peak day holds the annual multiplier across the full day.</item>
/// </list>
/// </summary>
/// <remarks>
/// In plain English: real banking traffic is not flat — it swells at lunchtime, spikes hard on payday
/// morning, and runs high all day on the busiest shopping days. This turns a simulated clock reading
/// into "how many times the average rate are we running right now", so the driver can shape the load to
/// match. It is a PURE function of the simulated instant (no clock read, no randomness) so a run is
/// reproducible.
/// </remarks>
public sealed class PeakEnvelope(WorkloadSpec spec)
{
    // §8.2 daily-peak windows in Lisbon local time. The simulated clock is UTC; Lisbon is UTC+0/+1.
    // The harness uses UTC hour bands as a deterministic stand-in for the named local windows — the
    // SHAPE (two daily humps) is what the spec fixes; exact tz handling is a calibration detail.
    private static readonly (int FromHour, int ToHour)[] DailyPeakWindows =
        [(12, 14), (18, 21)];

    // §8.2 payday mornings: the 1st and 25th of the month concentrate salary credits + standing orders.
    private static readonly int[] PaydayDays = [1, 25];

    private const int PaydayPeakHour = 9;            // "payday morning"
    private const int MonthlyPeakDurationMinutes = 15; // §8.2: ~10–15 minutes

    /// <summary>
    /// The instantaneous rate multiplier at <paramref name="simulatedNow"/> — the largest of the
    /// daily/monthly/annual contributions that apply (peaks compose by taking the dominant shape, not
    /// by multiplying, so a payday-at-lunch does not stack to an unrealistic 30×).
    /// </summary>
    /// <param name="simulatedNow">The simulated instant (UTC) from <see cref="SimulatedClock"/>.</param>
    /// <param name="annualPeakDay">The single simulated annual-peak day (§8.2: at least one per RC suite).</param>
    public double MultiplierAt(DateTimeOffset simulatedNow, DateOnly annualPeakDay)
    {
        var multiplier = 1.0;

        if (IsDailyPeak(simulatedNow))
        {
            multiplier = Math.Max(multiplier, spec.DailyPeakMultiplier);
        }

        if (IsMonthlyPeak(simulatedNow))
        {
            multiplier = Math.Max(multiplier, spec.MonthlyPeakMultiplier);
        }

        if (DateOnly.FromDateTime(simulatedNow.UtcDateTime) == annualPeakDay)
        {
            multiplier = Math.Max(multiplier, spec.AnnualPeakMultiplier);
        }

        return multiplier;
    }

    internal static bool IsDailyPeak(DateTimeOffset now)
    {
        var hour = now.UtcDateTime.Hour;
        foreach (var (from, to) in DailyPeakWindows)
        {
            if (hour >= from && hour < to)
            {
                return true;
            }
        }

        return false;
    }

    internal static bool IsMonthlyPeak(DateTimeOffset now)
    {
        var t = now.UtcDateTime;
        if (Array.IndexOf(PaydayDays, t.Day) < 0)
        {
            return false;
        }

        // The payday spike is a tight ~15-minute morning window, not the whole payday.
        return t.Hour == PaydayPeakHour && t.Minute < MonthlyPeakDurationMinutes;
    }
}
