using Babelstone.Engine;
using Babelstone.Families.CurrentAccount;
using Babelstone.FinancialMath;
using Babelstone.FinancialTypes;
using Xunit;

namespace Babelstone.Families.CurrentAccount.Application.Tests;

/// <summary>
/// The ARRANGED_OVERDRAFT_PACK_BOUNDED commitment (ADR-PC-037 §D5, commitment CA-1): the current_account
/// arranged overdraft (descoberto autorizado) is a pack-bounded authorization, and the overdraft interest
/// accrual against a negative balance conserves to the cent. In plain English: the family reads the
/// arranged-overdraft limit from the account's product config and lets a debit overdraw the account up to
/// that limit — beyond it (ultrapassagem) the debit is refused — and it computes the overdraft fee on the
/// drawn balance exactly, to the cent.
/// </summary>
/// <remarks>
/// <para>
/// Two halves, both Docker-free (the CA-1 "unit" lane):
/// </para>
/// <para>
/// <b>The authorization boundary.</b> These chain the REAL shipped <c>ca_pt_standard</c> product config
/// (<c>product-configs/current-account/ca_pt_standard.yaml</c>, read off disk by the family's own
/// <see cref="CurrentAccountProductConfigStore"/>) through <see cref="CurrentAccountProductConfig.ToAuthorizationRules"/>
/// into the pure <see cref="CurrentAccountAuthorizeDecider"/> — the same path the running authorize service
/// takes, minus the event store. So the OVERDRAFT_LIMIT_EXCEEDED / LIMIT_EXCEEDED arms are exercised with
/// production-resolved rules, not hand-written ones: the pack-value read is proven end-to-end short of
/// Postgres (that full HTTP path is the Integration-tagged <c>CurrentAccountAuthorizeApiIntegrationTests</c>).
/// </para>
/// <para>
/// <b>The accrual.</b> The descoberto interest/fee on a used overdraft is <see cref="Accrual.DailyBalanceInterest"/>
/// over a NEGATIVE balance — the demand-account daily-balance primitive (fin-math §8.2), which guards only
/// the time dimension, so a negative drawn balance yields the negative interest (the fee owed) through the
/// single <see cref="Money.FromCents"/> rounding boundary (ADR-PC-010 §P1–§P2, HALF_EVEN once). This is
/// command-side math, never a fold (ADR-PC-037 §P2).
/// </para>
/// </remarks>
public sealed class CurrentAccountOverdraftTests
{
    // The disk-backed production loader: walk up from the test binary to the repo's committed
    // product-configs/current-account/ tree (mirrors term-deposit's PartialWithdrawalConfigDiskTests).
    private static readonly CurrentAccountProductConfigStore Store = new(configsDir: null);

    private static readonly Guid AccountId = Guid.NewGuid();
    private static readonly DateOnly ValueDate = new(2026, 3, 5);

    private static AccountPosition ActiveStandardAccount() =>
        AccountPosition.Empty with
        {
            AccountId = AccountId,
            ProductCode = "ca_pt_standard",
            Currency = "EUR",
            Lifecycle = AccountLifecycle.Active,
        };

    private static AuthorizationRules StandardRules() =>
        Store.Resolve("ca_pt_standard")!.ToAuthorizationRules();

    private static DomainEvent Authorize(long amountCents, long availableBalanceCents) =>
        CurrentAccountAuthorizeDecider.Decide(
            ActiveStandardAccount(),
            new AuthorizationRequest(AccountId, AccountId.ToString(), "hold-under-test", new Money(amountCents), ValueDate),
            availableBalanceCents,
            StandardRules(),
            activeFreeze: null);

    // --- the shipped config resolves to the expected rules ---

    [Fact]
    public void The_shipped_ca_pt_standard_config_resolves_its_arranged_overdraft_and_per_transaction_cap()
    {
        var config = Store.Resolve("ca_pt_standard");
        Assert.NotNull(config);
        Assert.Equal(50_000, config!.ArrangedOverdraftLimitCents); // EUR 500 arranged overdraft
        Assert.Equal(500_000, config.PerTransactionLimitCents); // EUR 5 000 per-transaction cap

        Assert.Equal(new AuthorizationRules(50_000, 500_000), config.ToAuthorizationRules());
    }

    [Fact]
    public void The_shipped_ca_pt_basic_config_resolves_to_the_zero_overdraft_degenerate()
    {
        var config = Store.Resolve("ca_pt_basic");
        Assert.NotNull(config);
        Assert.Equal(0, config!.ArrangedOverdraftLimitCents);
        Assert.Null(config.PerTransactionLimitCents);

        // A ca_pt_basic account carries no headroom and no ceiling — equal to the degenerate rules.
        Assert.Equal(CurrentAccountProductConfig.None, config.ToAuthorizationRules());
    }

    [Fact]
    public void A_product_code_the_store_has_no_config_for_resolves_null_and_the_service_falls_back_to_None()
    {
        Assert.Null(Store.Resolve("ca_pt_unregistered"));
        // The service maps that null onto the zero-overdraft degenerate (the conservative no-headroom gate).
        Assert.Equal(new AuthorizationRules(), CurrentAccountProductConfig.None);
    }

    // --- ARRANGED_OVERDRAFT_PACK_BOUNDED: the authorization boundary, on the shipped config ---

    [Fact]
    public void ARRANGED_OVERDRAFT_PACK_BOUNDED_a_debit_within_the_arranged_overdraft_authorizes()
    {
        // Balance 0, the shipped EUR 500 (50 000-cent) arranged overdraft; a EUR 400 debit overdraws WITHIN
        // the limit (available − amount = −40 000 ≥ −50 000) → authorized, earmarking the hold.
        var result = Authorize(amountCents: 40_000, availableBalanceCents: 0);

        var hold = Assert.IsType<HoldPlaced>(result);
        Assert.Equal(40_000, hold.Amount.Cents);
    }

    [Fact]
    public void ARRANGED_OVERDRAFT_PACK_BOUNDED_an_unarranged_overdraft_is_refused_OVERDRAFT_LIMIT_EXCEEDED()
    {
        // Balance 0, a EUR 600 debit overdraws BEYOND the EUR 500 arranged limit (available − amount =
        // −60 000 < −50 000) → refused; and because an overdraft WAS arranged, the family names it
        // OVERDRAFT_LIMIT_EXCEEDED (ultrapassagem / descoberto não autorizado), not a plain shortfall.
        var result = Authorize(amountCents: 60_000, availableBalanceCents: 0);

        var declined = Assert.IsType<AuthorizationDeclined>(result);
        Assert.Equal(AccountDeclinedReason.OverdraftLimitExceeded, declined.DeclinedReason);
    }

    [Fact]
    public void ARRANGED_OVERDRAFT_PACK_BOUNDED_a_debit_over_the_per_transaction_cap_is_refused_LIMIT_EXCEEDED()
    {
        // Ample balance, but a EUR 6 000 debit exceeds the shipped EUR 5 000 per-transaction cap — refused
        // BEFORE the funds arithmetic (a rule breach is a rule breach regardless of balance).
        var result = Authorize(amountCents: 600_000, availableBalanceCents: 100_000_000);

        var declined = Assert.IsType<AuthorizationDeclined>(result);
        Assert.Equal(AccountDeclinedReason.LimitExceeded, declined.DeclinedReason);
    }

    [Fact]
    public void ARRANGED_OVERDRAFT_PACK_BOUNDED_a_debit_exactly_at_the_arranged_limit_authorizes_the_boundary_is_inclusive()
    {
        // Balance 0, a debit of exactly EUR 500 lands the available balance at −50 000 = −overdraft, which
        // the stage-4 gate PASSES (available − amount = −50 000 is not < −50 000).
        var result = Authorize(amountCents: 50_000, availableBalanceCents: 0);

        Assert.IsType<HoldPlaced>(result);
    }

    // --- ARRANGED_OVERDRAFT_PACK_BOUNDED: the descoberto accrual conserves to the cent ---

    [Fact]
    public void ARRANGED_OVERDRAFT_PACK_BOUNDED_the_descoberto_accrual_against_a_negative_balance_conserves_to_the_cent()
    {
        // EUR 1 000 drawn (a −100 000-cent balance) for a full 365-day year at an overdraft TAN of 20.00%
        // (2000 bps), Act/365: the fee is 20% × EUR 1 000 = EUR 200, i.e. −20 000 cents. The negative
        // balance yields the negative interest (the fee owed) — the demand-account primitive does not guard
        // the balance sign, only the time dimension.
        var fee = Accrual.DailyBalanceInterest(
            [(new Money(-100_000), 365)], rateBps: 2000, basis: 365);
        Assert.Equal(-20_000, fee.Cents);
    }

    [Fact]
    public void ARRANGED_OVERDRAFT_PACK_BOUNDED_the_descoberto_accrual_sums_a_stepped_negative_balance_to_the_cent()
    {
        // A step function: EUR 500 drawn for 10 days, then EUR 1 000 drawn for 20 days, at 18.25% (1825 bps),
        // Act/365. Σ(balance × days) = −2 500 000 cents·days; fee = 1825 × −2 500 000 / (365 × 10 000) =
        // −1 250 cents = −EUR 12.50 exactly. The whole numerator accumulates in decimal, rounded once.
        var fee = Accrual.DailyBalanceInterest(
            [(new Money(-50_000), 10), (new Money(-100_000), 20)], rateBps: 1825, basis: 365);
        Assert.Equal(-1_250, fee.Cents);
    }

    [Fact]
    public void ARRANGED_OVERDRAFT_PACK_BOUNDED_the_descoberto_accrual_rounds_once_at_the_cent_boundary()
    {
        // EUR 500 drawn for 33 days at 18.75% (1875 bps), Act/365 gives 1875 × −1 650 000 / 3 650 000 =
        // −847.6027… cents, which the single Money.FromCents boundary rounds HALF_EVEN to −848 cents — the
        // rounding happens once, at the decimal→cents boundary, not mid-calculation (ADR-PC-010 §P1).
        var fee = Accrual.DailyBalanceInterest(
            [(new Money(-50_000), 33)], rateBps: 1875, basis: 365);
        Assert.Equal(-848, fee.Cents);
    }

    [Fact]
    public void ARRANGED_OVERDRAFT_PACK_BOUNDED_the_descoberto_accrual_breaks_an_exact_half_cent_to_even()
    {
        // An EXACT −2.5-cent half: HALF_EVEN (banker's rounding, MidpointRounding.ToEven) resolves it to
        // the EVEN neighbour −2, where away-from-zero / HALF_UP would give −3 — so this uniquely pins that
        // the single Money.FromCents boundary rounds ToEven, not away from zero (the −847.6 case above
        // proves rounding happens but not the tie-break direction). EUR 5 drawn for 10 days at 18.25%
        // (1825 bps), Act/365: 1825 × −5 000 / (365 × 10 000) = −2.5 cents → −2.
        var fee = Accrual.DailyBalanceInterest(
            [(new Money(-500), 10)], rateBps: 1825, basis: 365);
        Assert.Equal(-2, fee.Cents);
    }
}
