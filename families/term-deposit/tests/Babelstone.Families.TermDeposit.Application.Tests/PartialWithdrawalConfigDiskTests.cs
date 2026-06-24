using Babelstone.RateSheets;
using Xunit;

namespace Babelstone.Families.TermDeposit.Application.Tests;

/// <summary>
/// k6r8.9 + k6r8.8 end-to-end on real committed variants: the disk-backed
/// <see cref="YamlProductConfigStore"/> reads the optional <c>partial_withdrawal</c> block off a
/// product-config YAML into the engine's <see cref="ProductConfig"/> primitives, and
/// <see cref="PartialWithdrawalPolicy.FromProductConfig"/> resolves the policy the F.12 decider takes.
/// The partial-withdrawal variant DECLARES the block; the walking-skeleton variant OMITS it (⇒ Unrestricted).
/// Pure disk read — no Postgres, so this is a plain unit test, not the Integration-tagged runtime path.
/// </summary>
public sealed class PartialWithdrawalConfigDiskTests
{
    // The production loader: walk up from the test binary to the repo's committed product-configs/ tree.
    private static readonly IProductConfigStore Store = new YamlProductConfigStore(productConfigsDir: null);

    [Fact]
    public void Resgate_parcial_variant_resolves_its_declared_partial_withdrawal_policy()
    {
        var config = Store.Resolve("dpz_pt_12m_resgate_parcial");
        Assert.NotNull(config);
        Assert.Equal(50_000, config!.MinWithdrawalCents);
        Assert.Equal(100_000, config.MinRemainingBalanceCents);
        Assert.Equal(90, config.CarenciaDays);

        Assert.Equal(
            new PartialWithdrawalPolicy(50_000, 100_000, 90),
            PartialWithdrawalPolicy.FromProductConfig(config));
    }

    [Fact]
    public void A_variant_without_the_block_resolves_Unrestricted()
    {
        var config = Store.Resolve("dpz_pt_12m_juros_venc");
        Assert.NotNull(config);
        Assert.Equal(0, config!.MinWithdrawalCents);
        Assert.Equal(0, config.MinRemainingBalanceCents);
        Assert.Equal(0, config.CarenciaDays);

        Assert.Same(PartialWithdrawalPolicy.Unrestricted, PartialWithdrawalPolicy.FromProductConfig(config));
    }
}
