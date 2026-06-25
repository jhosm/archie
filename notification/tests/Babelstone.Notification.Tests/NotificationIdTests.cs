using Babelstone.Notification;
using Xunit;

namespace Babelstone.Notification.Tests;

/// <summary>
/// Tests for <see cref="NotificationId"/> — the family-agnostic composite-id primitive (ADR-PC-025 slot 4).
/// The id must be deterministic (replay-stable: the same three inputs always yield the same id, so a rebuild
/// does not re-notify) and must distinguish all three composite parts.
/// </summary>
public sealed class NotificationIdTests
{
    [Fact]
    public void Compute_is_deterministic_and_distinguishes_the_three_composite_parts()
    {
        var instance = Guid.NewGuid();
        var other = Guid.NewGuid();
        var maturity = new DateOnly(2026, 7, 1);
        const string templateRef = "pt.notice.maturity";

        var id = NotificationId.Compute(instance, templateRef, maturity);

        // Stable: the same three inputs always yield the same id (replay-stable — slot 4).
        Assert.Equal(id, NotificationId.Compute(instance, templateRef, maturity));

        // Each of the three parts is load-bearing: changing any one changes the id.
        Assert.NotEqual(id, NotificationId.Compute(other, templateRef, maturity));
        Assert.NotEqual(id, NotificationId.Compute(instance, "pt.notice.other", maturity));
        Assert.NotEqual(id, NotificationId.Compute(instance, templateRef, maturity.AddDays(1)));

        // It is a well-formed RFC-4122 v5 GUID (name-based, deterministic) — never the zero GUID. The
        // version nibble is the high nibble of the time_hi_and_version field; in the
        // System.Guid.ToByteArray() layout (mixed-endian Data3) that is byte index 6.
        Assert.NotEqual(Guid.Empty, id);
        Assert.Equal(5, (id.ToByteArray()[6] >> 4) & 0x0F);
    }
}
