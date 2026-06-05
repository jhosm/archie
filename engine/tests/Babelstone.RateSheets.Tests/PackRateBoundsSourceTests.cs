using Babelstone.Packs;
using Babelstone.RateSheets;
using Babelstone.RateSheets.Api;
using Xunit;

namespace Babelstone.RateSheets.Tests;

/// <summary>
/// The §P2 rate bound is read from the VERIFIED pack keyed on the sheet's <c>pack_version</c>
/// (ADR-PC-008 §P2, C.5), not a host config knob: <see cref="PackRateBoundsSource"/> resolves
/// <c>[0, max_consumer_rate_bps]</c> from the pack the loader cached, and an unloaded
/// <c>pack_version</c> surfaces as a <see cref="PackLoadException"/> (which the deploy handler
/// maps to a clean 400, exercised end-to-end in the integration tests).
/// </summary>
public sealed class PackRateBoundsSourceTests
{
    [Fact]
    public void Resolves_the_ceiling_from_the_verified_pack_keyed_on_pack_version()
    {
        // Two pre-loaded packs with different ceilings: the source must read the one matching the
        // requested pack_version, proving the bound is pack-derived and version-keyed.
        var store = new StubPackStore(new Dictionary<string, int>
        {
            ["pt.2026.1"] = 2000,
            ["pt.2027.1"] = 1800,
        });
        var source = new PackRateBoundsSource(store);

        var first = source.For("pt.2026.1");
        var second = source.For("pt.2027.1");

        Assert.Equal(0, first.MinBasisPoints);
        Assert.Equal(2000, first.MaxBasisPoints);
        Assert.Equal(1800, second.MaxBasisPoints);
    }

    [Fact]
    public void An_unloaded_pack_version_fails_loud_rather_than_defaulting()
    {
        var source = new PackRateBoundsSource(
            new StubPackStore(new Dictionary<string, int> { ["pt.2026.1"] = 2000 }));

        // An unknown pin must NOT silently fall back to a default ceiling — it throws, and the
        // deploy handler turns that into a 400 (never a 500).
        Assert.Throws<PackLoadException>(() => source.For("pt.9999.1"));
    }

    [Fact]
    public async Task Reads_the_real_pt2026_pack_ceiling_off_disk()
    {
        // The disk-backed HostPackStore is the walking-skeleton stand-in for the OCI loader: it
        // structurally parses the committed packs/pt.2026.1 tree, so the bound is the real signed
        // pack's max_consumer_rate_bps (2000), not a configured number.
        var store = new HostPackStore(packsDir: null);
        await store.GetAsync("pt.2026.1");
        var source = new PackRateBoundsSource(store);

        var bounds = source.For("pt.2026.1");

        Assert.Equal(0, bounds.MinBasisPoints);
        Assert.Equal(2000, bounds.MaxBasisPoints);
    }

    [Fact]
    public void HostPackStore_resolve_before_load_fails_loud()
    {
        // Resolve is the pure hot path: a version never pre-loaded throws rather than reading disk,
        // mirroring OciPackStore's load-time/hot-path split.
        var store = new HostPackStore(packsDir: null);

        Assert.Throws<PackLoadException>(() => store.Resolve("pt.2026.1"));
    }

    /// <summary>An <see cref="IPackStore"/> whose cached packs carry only the ceiling under test.</summary>
    private sealed class StubPackStore(IReadOnlyDictionary<string, int> ceilings) : IPackStore
    {
        public Task<VerifiedPack> GetAsync(string packVersion, CancellationToken ct = default)
            => throw new NotSupportedException("the stub is pre-loaded; the source only calls Resolve.");

        public VerifiedPack Resolve(string packVersion)
            => ceilings.TryGetValue(packVersion, out var ceiling)
                ? PackTestStubs.WithMaxConsumerRateBps(packVersion, ceiling)
                : throw new PackLoadException(packVersion, null, "not pre-loaded (stub).");
    }
}
