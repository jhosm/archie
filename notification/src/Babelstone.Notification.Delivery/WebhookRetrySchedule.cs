namespace Babelstone.Notification.Delivery;

/// <summary>
/// The ADR-IC-011 §D4 retry schedule — exponential backoff with ±25% jitter. In plain terms: a failed
/// delivery is retried on a widening schedule (30s, 2m, 8m, 30m, then 2h steps) so a recovering receiver
/// is not hammered, with jitter so many deliveries retrying against one recovering receiver do not arrive
/// as a thundering herd. Ten attempts (~12 hours) and the delivery is dead-lettered — the §D4 exhaustion
/// rule the pass enforces via <see cref="WebhookDeliveryOptions.MaxAttempts"/>.
/// </summary>
public static class WebhookRetrySchedule
{
    /// <summary>The jitter half-width (±25%, ADR-IC-011 §D4).</summary>
    public const double JitterFraction = 0.25;

    /// <summary>
    /// The un-jittered §D4 base delay before the NEXT attempt, given how many attempts have failed so
    /// far: 1 failure → 30s (attempt 2), 2 → 2m, 3 → 8m, 4 → 30m, 5+ → 2h (attempts 6–10).
    /// </summary>
    public static TimeSpan BaseDelay(int failedAttempts)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(failedAttempts, 1);
        return failedAttempts switch
        {
            1 => TimeSpan.FromSeconds(30),
            2 => TimeSpan.FromMinutes(2),
            3 => TimeSpan.FromMinutes(8),
            4 => TimeSpan.FromMinutes(30),
            _ => TimeSpan.FromHours(2),
        };
    }

    /// <summary>
    /// The jittered delay before the next attempt: <see cref="BaseDelay"/> scaled by
    /// <c>1 + 0.25 × jitterUnit</c>. <paramref name="jitterUnit"/> is a caller-supplied value in
    /// <c>[-1, 1]</c> (production draws it uniformly; a test pins it), keeping this function pure — no
    /// hidden randomness, the same determinism stance as the rest of the estate.
    /// </summary>
    public static TimeSpan NextDelay(int failedAttempts, double jitterUnit)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(jitterUnit, -1.0);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(jitterUnit, 1.0);

        var baseDelay = BaseDelay(failedAttempts);
        return TimeSpan.FromTicks((long)(baseDelay.Ticks * (1.0 + (JitterFraction * jitterUnit))));
    }
}
