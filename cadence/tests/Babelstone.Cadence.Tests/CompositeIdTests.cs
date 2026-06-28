using Babelstone.Cadence;
using Xunit;

namespace Babelstone.Cadence.Tests;

/// <summary>
/// Tests for <see cref="CompositeId"/> — the generic composite-id primitive (ADR-PC-036 §Decision 2 +
/// ADR-IC-019 / ADR-PC-025 slot 4). The id must be deterministic (replay-stable: the same ordered parts always
/// yield the same id, so a rebuild does not re-act), must distinguish the parts positionally, and must be a
/// well-formed RFC-4122 v5 GUID.
/// </summary>
public sealed class CompositeIdTests
{
    [Fact]
    public void Compute_is_deterministic_and_order_sensitive_across_parts()
    {
        var a = Guid.NewGuid().ToString("D");
        var b = "pt.notice.maturity";
        var c = new DateOnly(2026, 7, 1).ToString("yyyy-MM-dd");

        var id = CompositeId.Compute(a, b, c);

        // Stable: the same ordered parts always yield the same id (replay-stable — slot 4).
        Assert.Equal(id, CompositeId.Compute(a, b, c));

        // Each part is load-bearing: changing any one changes the id.
        Assert.NotEqual(id, CompositeId.Compute(Guid.NewGuid().ToString("D"), b, c));
        Assert.NotEqual(id, CompositeId.Compute(a, "pt.notice.other", c));
        Assert.NotEqual(id, CompositeId.Compute(a, b, new DateOnly(2026, 7, 2).ToString("yyyy-MM-dd")));

        // Order is load-bearing — the id distinguishes the parts positionally, not as a set.
        Assert.NotEqual(CompositeId.Compute(a, b, c), CompositeId.Compute(b, a, c));
    }

    [Fact]
    public void Compute_returns_a_well_formed_rfc4122_version5_guid()
    {
        var id = CompositeId.Compute("one", "two");

        // Never the zero GUID, and a well-formed RFC-4122 v5 (name-based, deterministic) value. The version
        // nibble is the high nibble of the time_hi_and_version field; in the System.Guid.ToByteArray() layout
        // (mixed-endian Data3) that is byte index 6, and the variant high bits live in byte 8.
        Assert.NotEqual(Guid.Empty, id);
        var bytes = id.ToByteArray();
        Assert.Equal(5, (bytes[6] >> 4) & 0x0F);
        Assert.Equal(0x80, bytes[8] & 0xC0);
    }
}
