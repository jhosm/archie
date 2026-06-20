using Babelstone.LoadHarness.Runner;
using Xunit;

namespace Babelstone.LoadHarness.Runner.Tests;

/// <summary>
/// Docker-free tests for the L.3c burst profile sequencing (bd babelstone-2e6q.3): the run plan ramps
/// from the sustained baseline up to a short, sharp 1000-TPS burst and back to recovery, in that order.
/// The §8.3 requirement is "1000 TPS for 15 min" reached AFTER the sustained baseline — the ordering is
/// load-bearing, so it is pinned here independently of a live run.
/// </summary>
public sealed class BurstProfileTests
{
    [Fact]
    public void Smoke_profile_is_a_single_phase()
    {
        var phases = LoadRunner.PlanPhases(new RunnerOptions { Profile = RunProfile.Smoke });
        var phase = Assert.Single(phases);
        Assert.Equal("smoke", phase.Label);
    }

    [Fact]
    public void Sustained_profile_holds_one_phase_at_the_target()
    {
        var options = new RunnerOptions { Profile = RunProfile.Sustained, TargetTps = 250 };
        var phase = Assert.Single(LoadRunner.PlanPhases(options));
        Assert.Equal("sustained", phase.Label);
        Assert.Equal(250, phase.TargetTps);
    }

    [Fact]
    public void Burst_profile_sequences_sustained_then_burst_then_recovery()
    {
        var options = new RunnerOptions
        {
            Profile = RunProfile.Burst,
            TargetTps = 250,
            BurstTps = 1000,
            Duration = TimeSpan.FromSeconds(60),
            BurstDuration = TimeSpan.FromMinutes(15),
        };

        var phases = LoadRunner.PlanPhases(options);

        Assert.Equal(3, phases.Count);
        // §8.3: the burst is REACHED after the sustained baseline, then recovery — the order matters.
        Assert.Equal(["sustained", "burst", "recovery"], phases.Select(p => p.Label));

        Assert.Equal(250, phases[0].TargetTps);
        Assert.Equal(1000, phases[1].TargetTps);                 // the §8.3 1000-TPS burst
        Assert.Equal(TimeSpan.FromMinutes(15), phases[1].Duration); // for 15 minutes
        Assert.Equal(250, phases[2].TargetTps);                  // recovery back to baseline
    }
}
