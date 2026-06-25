using Babelstone.RateSheets;
using Xunit;

namespace Babelstone.Families.TermDeposit.Application.Tests;

/// <summary>
/// bd babelstone-fk7m.9 (ADR-PC-009 §A2): the disk-backed <see cref="YamlProductConfigStore"/> stamps a
/// content-hash <see cref="ProductConfig.ConfigVersion"/> (SHA-256 of the YAML bytes) on every resolved
/// config — the per-instance pin the decider stamps onto <c>DepositConstituted</c>. These are pure disk
/// reads over the real committed product-configs/ tree (no Postgres), so the version is deterministic:
/// the same bytes hash to the same version on any host/replay, and two different configs differ.
/// </summary>
public sealed class ProductConfigVersionDiskTests
{
    // The production loader: walk up from the test binary to the repo's committed product-configs/ tree.
    private static readonly IProductConfigStore Store = new YamlProductConfigStore(productConfigsDir: null);

    [Fact]
    public void ConfigVersion_is_a_prefixed_sha256_content_hash()
    {
        var config = Store.Resolve("dpz_pt_12m_juros_venc");
        Assert.NotNull(config);
        // sha256: + 64 lowercase hex chars.
        Assert.StartsWith("sha256:", config!.ConfigVersion);
        Assert.Equal(7 + 64, config.ConfigVersion.Length);
        Assert.Matches("^sha256:[0-9a-f]{64}$", config.ConfigVersion);
    }

    [Fact]
    public void ConfigVersion_is_deterministic_across_loads()
    {
        // Two independent loads of the same committed file must produce the identical version — the pin
        // must be replay-stable, never host- or time-dependent (ADR-PC-010 §P5; REPLAY_PIN_PER_EVENT).
        var first = new YamlProductConfigStore(productConfigsDir: null).Resolve("dpz_pt_12m_juros_venc");
        var second = new YamlProductConfigStore(productConfigsDir: null).Resolve("dpz_pt_12m_juros_venc");
        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Equal(first!.ConfigVersion, second!.ConfigVersion);
    }

    [Fact]
    public void Different_product_configs_have_different_ConfigVersions()
    {
        // Distinct YAML files hash to distinct versions — the content hash actually discriminates the
        // generation, so a replay can tell which product-config governed a deposit.
        var venc = Store.Resolve("dpz_pt_12m_juros_venc");
        var mensal = Store.Resolve("dpz_pt_12m_juros_mensal");
        Assert.NotNull(venc);
        Assert.NotNull(mensal);
        Assert.NotEqual(venc!.ConfigVersion, mensal!.ConfigVersion);
    }

    [Fact]
    public void FromConfigs_seam_defaults_ConfigVersion_to_empty()
    {
        // The in-memory test seam takes ProductConfig instances directly; callers that do not set a
        // version get "" — the same empty default a pre-pin / no-store-wired constitution stamps.
        var store = YamlProductConfigStore.FromConfigs(
        [
            new ProductConfig("p1", 365, "AT_MATURITY", "NONE", 0, "standard"),
        ]);
        Assert.Equal("", store.Resolve("p1")!.ConfigVersion);
    }
}
