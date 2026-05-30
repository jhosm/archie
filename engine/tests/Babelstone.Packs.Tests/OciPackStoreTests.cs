using Xunit;

namespace Babelstone.Packs.Tests;

/// <summary>
/// Offline tests of the load-time flow + fail-loud branches + the immutable cache, driven by
/// fakes (no real oras/cosign). Default CI lane.
/// </summary>
public sealed class OciPackStoreTests
{
    private static readonly PackRef Ref = new("oci-layout:/tmp/pt", "sha256:image", "sha256:signature");

    private static InMemoryPackVersionRegistry Registry(string pin = "pt.2026.1")
        => new(new Dictionary<string, PackRef> { [pin] = Ref });

    [Fact]
    public async Task GetAsync_resolves_verifies_pulls_parses_and_caches()
    {
        var source = new FakePackSource(PackTestData.LoadPt2026());
        var verifier = new FakePackVerifier();
        var store = new OciPackStore(Registry(), verifier, source);

        var pack = await store.GetAsync("pt.2026.1");

        Assert.Equal("pt.2026.1", pack.VersionKey);
        Assert.Same(pack, await store.GetAsync("pt.2026.1")); // cache hit returns the same instance
        Assert.Same(pack, store.Resolve("pt.2026.1"));        // pure hot-path read
        Assert.Equal(1, source.CallCount);                    // pulled exactly once
        Assert.Equal(1, verifier.CallCount);                  // verified exactly once
    }

    [Fact]
    public void Resolve_before_load_fails_loud()
    {
        var store = new OciPackStore(Registry(), new FakePackVerifier(), new FakePackSource(PackTestData.LoadPt2026()));
        Assert.Throws<PackLoadException>(() => store.Resolve("pt.2026.1"));
    }

    [Fact]
    public async Task An_unknown_pin_fails_loud()
    {
        var store = new OciPackStore(Registry(), new FakePackVerifier(), new FakePackSource(PackTestData.LoadPt2026()));
        await Assert.ThrowsAsync<PackLoadException>(() => store.GetAsync("pt.9999.1"));
    }

    [Fact]
    public async Task Verify_runs_before_pull_and_a_failure_aborts_the_load()
    {
        var source = new FakePackSource(PackTestData.LoadPt2026());
        var store = new OciPackStore(Registry(), new ThrowingPackVerifier(), source);

        await Assert.ThrowsAsync<PackLoadException>(() => store.GetAsync("pt.2026.1"));
        Assert.Equal(0, source.CallCount); // a failed cosign verify must never reach the pull
    }

    [Fact]
    public async Task A_pull_failure_fails_loud()
    {
        var store = new OciPackStore(Registry(), new FakePackVerifier(), new ThrowingPackSource());
        await Assert.ThrowsAsync<PackLoadException>(() => store.GetAsync("pt.2026.1"));
    }

    [Fact]
    public async Task A_registry_mismapping_is_caught_by_the_version_key_cross_check()
    {
        // The registry maps pt.2026.2 -> a ref, but the pulled content is pt.2026.1: fail loud.
        var store = new OciPackStore(Registry("pt.2026.2"), new FakePackVerifier(), new FakePackSource(PackTestData.LoadPt2026()));
        var ex = await Assert.ThrowsAsync<PackLoadException>(() => store.GetAsync("pt.2026.2"));
        Assert.Contains("version-key mismatch", ex.Message);
    }
}
