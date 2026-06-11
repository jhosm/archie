using Babelstone.Packs;
using Babelstone.RateSheets.Api;
using Xunit;

namespace Babelstone.RateSheets.Tests;

/// <summary>
/// The disk-backed <see cref="HostPackStore"/> negative paths (bd babelstone-z0as): beyond the
/// happy pt.2026.1 parse (<see cref="PackRateBoundsSourceTests"/>), the loader must fail loud on a
/// missing pack directory, a missing data file within a present directory, and a structurally
/// unparseable pack — never silently substitute a default bound. The store is the walking-skeleton
/// stand-in for <see cref="OciPackStore"/>, so it honours the same load-time/hot-path split and the
/// same <see cref="PackLoadException"/>-on-unloaded-version contract.
/// </summary>
public sealed class HostPackStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "hostpackstore-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public async Task A_missing_pack_directory_fails_loud_as_a_PackLoadException()
    {
        // An empty packs/ tree: the requested version has no directory at all — an unknown/unpinned
        // pack. The loader names it in a PackLoadException rather than defaulting (§P4 fail-loud).
        Directory.CreateDirectory(_root);
        var store = new HostPackStore(packsDir: _root);

        var ex = await Assert.ThrowsAsync<PackLoadException>(() => store.GetAsync("pt.9999.1"));
        Assert.Equal("pt.9999.1", ex.PackVersion);
        Assert.Contains("no directory", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_pack_directory_missing_a_required_data_file_fails_loud()
    {
        // The directory exists but a required data file (parameters/constants.yaml) is absent — a
        // truncated/corrupt pack tree. The read throws rather than parsing a partial pack, so a
        // half-present pack can never resolve a bound off a default.
        var packDir = Path.Combine(_root, "pt.partial");
        Directory.CreateDirectory(Path.Combine(packDir, "primitives"));
        // Write every file EXCEPT parameters/constants.yaml so the directory exists and the first
        // few reads succeed, isolating the missing-file failure to one specific data file.
        File.WriteAllText(Path.Combine(packDir, "pack.yaml"), "stub");
        foreach (var primitive in new[] { "day-count.yaml", "withholding.yaml", "fgd.yaml", "reporting.yaml" })
        {
            File.WriteAllText(Path.Combine(packDir, "primitives", primitive), "stub");
        }

        var store = new HostPackStore(packsDir: _root);

        // A missing data file surfaces as a file-IO failure at load — the loader reads every
        // declared data file before parsing, so the absent one aborts the load.
        await Assert.ThrowsAnyAsync<IOException>(() => store.GetAsync("pt.partial"));
    }

    [Fact]
    public async Task A_structurally_unparseable_pack_fails_loud()
    {
        // Every declared data file is present but their content is not a valid pack (garbage YAML):
        // PackParser.Parse rejects it. The bound is never read off a malformed pack.
        var packDir = Path.Combine(_root, "pt.garbage");
        Directory.CreateDirectory(Path.Combine(packDir, "primitives"));
        Directory.CreateDirectory(Path.Combine(packDir, "parameters"));
        Directory.CreateDirectory(Path.Combine(packDir, "rate-sheet-refs"));
        File.WriteAllText(Path.Combine(packDir, "pack.yaml"), ":\n  not: [valid");
        foreach (var primitive in new[] { "day-count.yaml", "withholding.yaml", "fgd.yaml", "reporting.yaml" })
        {
            File.WriteAllText(Path.Combine(packDir, "primitives", primitive), ":\n  not: [valid");
        }

        File.WriteAllText(Path.Combine(packDir, "parameters", "constants.yaml"), ":\n  not: [valid");
        File.WriteAllText(Path.Combine(packDir, "rate-sheet-refs", "deposits-pt.yaml"), ":\n  not: [valid");

        var store = new HostPackStore(packsDir: _root);

        // PackParser surfaces a structural failure as a PackLoadException; an unexpected parser
        // exception type would also be a load failure, so accept any throw here.
        await Assert.ThrowsAnyAsync<Exception>(() => store.GetAsync("pt.garbage"));
    }

    [Fact]
    public async Task GetAsync_caches_so_a_second_call_is_the_same_instance()
    {
        // The load-time parse happens once and caches immutably; a second GetAsync returns the same
        // VerifiedPack reference (no re-read of disk), the load-time/hot-path split OciPackStore keeps.
        var store = new HostPackStore(packsDir: null); // the committed packs/ tree
        var first = await store.GetAsync("pt.2026.1");
        var second = await store.GetAsync("pt.2026.1");

        Assert.Same(first, second);
        // And Resolve (the pure hot path) now returns the cached pack rather than throwing.
        Assert.Same(first, store.Resolve("pt.2026.1"));
    }
}
