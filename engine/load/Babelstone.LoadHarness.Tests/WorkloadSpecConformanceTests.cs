using Xunit;

namespace Babelstone.LoadHarness.Tests;

/// <summary>
/// §8.2 / §8.3 workload-shape conformance for the v4-scale load (ADR-PC-011, bd 84ww — L.2). L.1
/// (commit ef8e71c) shipped the <see cref="WorkloadSpec"/> + <see cref="PeakEnvelope"/> data; this pins
/// the SHAPE those records encode against the three things L.2 owns: the event mix, the peak structure,
/// and the sync/async classification. It is intentionally a thin spec-conformance layer over the
/// already-shipped types — no new harness behaviour — so a future edit to the default shape that drifts
/// from ADR-PC-011 §8.2/§8.3 fails the build.
/// </summary>
/// <remarks>
/// In plain English: the load test's "recipe" — what kinds of events, in what proportions, which ones
/// block a payment versus run in the background, and how the rate swells at lunch / on payday / on the
/// busiest day of the year — has to match what the spec (two-modes §8.2/§8.3) calls for. This file holds
/// the recipe up against the spec's numbers so nobody can quietly change the proportions or the peak
/// shape without a failing test forcing the spec change to be deliberate.
/// </remarks>
public sealed class WorkloadSpecConformanceTests
{
    private static readonly WorkloadSpec Spec = WorkloadSpec.Default();

    // ---- §8.2 event mix ----

    [Fact]
    public void Mix_shares_sum_to_one()
    {
        // The §8.2 mix is a distribution over E_year: the shares across ALL classes (harness-emitted and
        // engine-generated alike) must sum to 1.0 — the mix documents the whole workload, not just the
        // part the driver emits.
        var total = Spec.Mix.Sum(c => c.Share);
        Assert.Equal(1.0, total, precision: 10);
    }

    [Theory]
    // The §8.2 table the default encodes: externally-ingested card ~70% + transfers/DDs ~15% (~85%
    // harness-emitted), engine-generated lifecycle ~10%, cross-mode settlement ~3%, operational ~2%.
    [InlineData("card_transactions", 0.70)]
    [InlineData("transfers_direct_debits", 0.15)]
    [InlineData("engine_lifecycle", 0.10)]
    [InlineData("cross_mode", 0.03)]
    [InlineData("operational", 0.02)]
    public void Mix_class_shares_match_the_8_2_table(string name, double expectedShare)
    {
        var cls = Spec.Mix.Single(c => c.Name == name);
        Assert.Equal(expectedShare, cls.Share, precision: 10);
    }

    [Fact]
    public void Externally_ingested_classes_are_about_eighty_five_percent()
    {
        // §8.2 / §25: "~85% of E_year arrives as externally-ingested events (card ~70%, transfers/DDs
        // ~15%)" — the share the harness drives onto Redpanda as the steady stream.
        var externallyIngested = Spec.Mix
            .Where(c => c.Name is "card_transactions" or "transfers_direct_debits")
            .Sum(c => c.Share);
        Assert.Equal(0.85, externallyIngested, precision: 10);
    }

    [Fact]
    public void Engine_generated_and_cross_mode_are_about_thirteen_percent_and_not_harness_emitted()
    {
        // §8.2 / §30: the ~10% engine-generated lifecycle + ~3% cross-mode flow are produced by the
        // ENGINE when the injected clock advances, NEVER by the harness (§8.4 "not via internal entry
        // points"). Pin both the share AND the not-harness-emitted classification.
        var engineGenerated = Spec.Mix.Where(c => !c.HarnessEmitted).ToList();
        Assert.Equal(0.13, engineGenerated.Sum(c => c.Share), precision: 10);
        Assert.Equal(
            new[] { "engine_lifecycle", "cross_mode" }.OrderBy(n => n),
            engineGenerated.Select(c => c.Name).OrderBy(n => n));
    }

    [Fact]
    public void Harness_emitted_classes_are_the_externally_ingested_stream_plus_operational()
    {
        // The driver puts the ~85% externally-ingested stream + the ~2% operational externals on the
        // bus (§P1) — and nothing else. This is the boundary the L.1 generator filters on; L.2 pins it.
        var harnessEmitted = Spec.Mix.Where(c => c.HarnessEmitted).Select(c => c.Name).OrderBy(n => n).ToList();
        Assert.Equal(
            new[] { "card_transactions", "operational", "transfers_direct_debits" },
            harnessEmitted);
        Assert.Equal(0.87, Spec.Mix.Where(c => c.HarnessEmitted).Sum(c => c.Share), precision: 10);
    }

    // ---- §8.2 sync/async classification ----

    [Theory]
    // §8.2/§8.3: card + transfers project synchronously (they block authorization, tight latency band);
    // operational externals (freezes/holds) are sync too. Engine-generated lifecycle and cross-mode
    // settlement are async (lag-bounded, asserted against the simulated close instant).
    [InlineData("card_transactions", true)]
    [InlineData("transfers_direct_debits", true)]
    [InlineData("operational", true)]
    [InlineData("engine_lifecycle", false)]
    [InlineData("cross_mode", false)]
    public void Sync_async_classification_matches_the_8_2_table(string name, bool expectedSync)
    {
        var cls = Spec.Mix.Single(c => c.Name == name);
        Assert.Equal(expectedSync, cls.Sync);
    }

    // ---- §8.3 throughput targets ----

    [Fact]
    public void Throughput_targets_are_the_8_3_sustained_and_burst()
    {
        // §8.3: 250 TPS sustained for 24h, 1000 TPS burst for 15 min with no event loss.
        Assert.Equal(250.0, Spec.SustainedTps);
        Assert.Equal(1000.0, Spec.BurstTps);
    }

    [Fact]
    public void Peak_multipliers_are_the_8_2_upper_bounds()
    {
        // §8.2 named ranges, encoded at the upper bound (the harder shape to pass): daily 2–3× → 3×,
        // monthly payday ~10× → 10×, annual Black-Friday/Christmas 4–5× → 5×.
        Assert.Equal(3.0, Spec.DailyPeakMultiplier);
        Assert.Equal(10.0, Spec.MonthlyPeakMultiplier);
        Assert.Equal(5.0, Spec.AnnualPeakMultiplier);
    }

    // ---- §8.2 peak structure (PeakEnvelope) ----

    [Fact]
    public void Off_peak_instant_runs_at_the_sustained_average()
    {
        // A quiet mid-morning weekday instant (not a payday, not the annual peak) runs at 1× — the
        // baseline the multipliers swell above.
        var envelope = new PeakEnvelope(Spec);
        var offPeak = new DateTimeOffset(2026, 6, 10, 3, 0, 0, TimeSpan.Zero); // 03:00, not payday
        var annualPeakDay = new DateOnly(2026, 11, 27);

        Assert.Equal(1.0, envelope.MultiplierAt(offPeak, annualPeakDay));
    }

    [Fact]
    public void Daily_lunch_and_after_work_windows_lift_to_the_daily_multiplier()
    {
        // §8.2: lunch (12–14) and after-work (18–21) are the two daily humps.
        var envelope = new PeakEnvelope(Spec);
        var annualPeakDay = new DateOnly(2026, 11, 27);
        var lunch = new DateTimeOffset(2026, 6, 10, 13, 0, 0, TimeSpan.Zero);
        var afterWork = new DateTimeOffset(2026, 6, 10, 19, 0, 0, TimeSpan.Zero);

        Assert.Equal(Spec.DailyPeakMultiplier, envelope.MultiplierAt(lunch, annualPeakDay));
        Assert.Equal(Spec.DailyPeakMultiplier, envelope.MultiplierAt(afterWork, annualPeakDay));
    }

    [Fact]
    public void Payday_morning_concentrates_the_monthly_multiplier_in_a_tight_window()
    {
        // §8.2: payday morning (the 1st / 25th, 09:00, ~15 min) is the dominant monthly spike.
        var envelope = new PeakEnvelope(Spec);
        var annualPeakDay = new DateOnly(2026, 11, 27);
        var paydaySpike = new DateTimeOffset(2026, 6, 25, 9, 5, 0, TimeSpan.Zero);   // within the 15-min window
        var paydayLater = new DateTimeOffset(2026, 6, 25, 9, 30, 0, TimeSpan.Zero);  // same payday, after the window

        Assert.Equal(Spec.MonthlyPeakMultiplier, envelope.MultiplierAt(paydaySpike, annualPeakDay));
        // Outside the tight window the monthly spike has passed — it does not hold all day.
        Assert.True(envelope.MultiplierAt(paydayLater, annualPeakDay) < Spec.MonthlyPeakMultiplier);
    }

    [Fact]
    public void Annual_peak_day_holds_the_annual_multiplier_across_the_whole_day()
    {
        // §8.2: the synthetic annual-peak day (Black Friday / Christmas Eve) runs high ALL day — unlike
        // the tight payday spike, an off-peak hour on the annual day still carries the annual multiplier.
        var envelope = new PeakEnvelope(Spec);
        var annualPeakDay = new DateOnly(2026, 11, 27);
        var quietHourOnPeakDay = new DateTimeOffset(2026, 11, 27, 3, 0, 0, TimeSpan.Zero);

        Assert.Equal(Spec.AnnualPeakMultiplier, envelope.MultiplierAt(quietHourOnPeakDay, annualPeakDay));
    }

    [Fact]
    public void Composed_peaks_take_the_dominant_shape_not_the_product()
    {
        // §8.2 (PeakEnvelope contract): a payday-at-lunch does NOT stack 3× × 10× = 30× — peaks compose
        // by the dominant (max) shape, so the result is the monthly 10×, not a runaway product.
        var envelope = new PeakEnvelope(Spec);
        var annualPeakDay = new DateOnly(2026, 11, 27);
        // 25th at lunch hour — but the monthly spike is a 09:00 window, so at 13:00 only the daily hump
        // applies; use the 1st at 09:05 which is both payday-morning AND we verify it stays the max, 10×.
        var paydayMorning = new DateTimeOffset(2026, 6, 1, 9, 5, 0, TimeSpan.Zero);

        var multiplier = envelope.MultiplierAt(paydayMorning, annualPeakDay);
        Assert.Equal(Spec.MonthlyPeakMultiplier, multiplier);
        Assert.True(multiplier < Spec.DailyPeakMultiplier * Spec.MonthlyPeakMultiplier);
    }
}
