using Babelstone.Notification.Delivery;
using Xunit;

namespace Babelstone.Notification.Delivery.Tests;

/// <summary>Pins the ADR-IC-011 §D4 backoff table (30s, 2m, 8m, 30m, then 2h steps) and the ±25%
/// jitter envelope — the schedule is a decided contract value, not an implementation detail.</summary>
public sealed class WebhookRetryScheduleTests
{
    [Theory]
    [InlineData(1, 30)]        // after the 1st failure → 30 seconds
    [InlineData(2, 120)]       // 2 minutes
    [InlineData(3, 480)]       // 8 minutes
    [InlineData(4, 1800)]      // 30 minutes
    [InlineData(5, 7200)]      // 2 hours (attempts 6–10)
    [InlineData(9, 7200)]
    public void Base_delay_follows_the_d4_table(int failedAttempts, int expectedSeconds)
    {
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), WebhookRetrySchedule.BaseDelay(failedAttempts));
    }

    [Fact]
    public void Jitter_scales_within_plus_minus_25_percent()
    {
        Assert.Equal(TimeSpan.FromSeconds(30), WebhookRetrySchedule.NextDelay(1, 0.0));
        Assert.Equal(TimeSpan.FromSeconds(22.5), WebhookRetrySchedule.NextDelay(1, -1.0));
        Assert.Equal(TimeSpan.FromSeconds(37.5), WebhookRetrySchedule.NextDelay(1, 1.0));
    }

    [Fact]
    public void Out_of_range_inputs_fail_loud()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => WebhookRetrySchedule.BaseDelay(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => WebhookRetrySchedule.NextDelay(1, 1.5));
        Assert.Throws<ArgumentOutOfRangeException>(() => WebhookRetrySchedule.NextDelay(1, -1.5));
    }
}
