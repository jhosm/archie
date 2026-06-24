using Babelstone.RateSheets;
using Xunit;

namespace Babelstone.Families.TermDeposit.Application.Tests;

/// <summary>
/// k6r8.8: the engine resolves the F.12 partial-withdrawal policy from a product's structural config at
/// the constitution boundary, exactly as it resolves the day-count and withholding primitives — the
/// decider then takes the resolved policy as an explicit input (ADR-PC-021 §D3). These pin the pure
/// mapping <see cref="PartialWithdrawalPolicy.FromProductConfig"/>: a config that DECLARES the gates
/// resolves a matching policy; a config that OMITS the block (all three gates zero) resolves
/// <see cref="PartialWithdrawalPolicy.Unrestricted"/> (02 §2.4.1). Pure — no clock, no I/O, no fixture.
/// </summary>
public sealed class PartialWithdrawalPolicyResolutionTests
{
    private static ProductConfig ConfigWith(long minWithdrawal, long minRemaining, int lockupPeriodDays) =>
        new(
            ProductId: "dpz_pt_12m_resgate_parcial",
            TermDays: 365,
            InterestVariant: "AT_MATURITY",
            AutoRenewalPolicy: "NONE",
            PaymentPeriodMonths: 0,
            DefaultRole: "standard",
            MinWithdrawalCents: minWithdrawal,
            MinRemainingBalanceCents: minRemaining,
            LockupPeriodDays: lockupPeriodDays);

    [Fact]
    public void A_config_declaring_the_gates_resolves_a_matching_policy()
    {
        var policy = PartialWithdrawalPolicy.FromProductConfig(ConfigWith(50_000, 100_000, 90));

        Assert.Equal(new PartialWithdrawalPolicy(50_000, 100_000, 90), policy);
    }

    [Fact]
    public void A_config_omitting_the_block_resolves_Unrestricted()
    {
        // An omitted partial_withdrawal block leaves all three primitives at their 0 default.
        var policy = PartialWithdrawalPolicy.FromProductConfig(ConfigWith(0, 0, 0));

        Assert.Same(PartialWithdrawalPolicy.Unrestricted, policy);
    }

    [Theory]
    [InlineData(50_000, 0, 0)]
    [InlineData(0, 100_000, 0)]
    [InlineData(0, 0, 90)]
    public void Any_single_non_zero_gate_resolves_a_real_policy_not_Unrestricted(
        long minWithdrawal, long minRemaining, int lockupPeriodDays)
    {
        var policy = PartialWithdrawalPolicy.FromProductConfig(
            ConfigWith(minWithdrawal, minRemaining, lockupPeriodDays));

        Assert.NotSame(PartialWithdrawalPolicy.Unrestricted, policy);
        Assert.Equal(new PartialWithdrawalPolicy(minWithdrawal, minRemaining, lockupPeriodDays), policy);
    }
}
