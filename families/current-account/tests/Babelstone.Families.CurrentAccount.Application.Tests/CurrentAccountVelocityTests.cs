using Babelstone.Engine;
using Babelstone.Families.CurrentAccount;
using Babelstone.FinancialTypes;
using Xunit;

namespace Babelstone.Families.CurrentAccount.Application.Tests;

/// <summary>
/// The VELOCITY_LIMIT_PACK_BOUNDED commitment (ADR-PC-037 §D5/§D6, commitment CA-2): the current_account
/// rolling daily/monthly velocity caps are pack-bounded authorizations. In plain English: a product config
/// can cap not just each single debit but the total authorized in a rolling day or month, and the authorize
/// decider refuses a debit that would push the window's total past its cap — even when the single amount and
/// the balance are both fine.
/// </summary>
/// <remarks>
/// <para>
/// Docker-free, and the sibling of <see cref="CurrentAccountOverdraftTests"/>: these chain the REAL shipped
/// <c>ca_pt_standard</c> product config (<c>product-configs/current-account/ca_pt_standard.yaml</c>, read off
/// disk by the family's own <see cref="CurrentAccountProductConfigStore"/>) through
/// <see cref="CurrentAccountProductConfig.ToAuthorizationRules"/> into the pure
/// <see cref="CurrentAccountAuthorizeDecider"/> — the same path the running authorize service takes, minus the
/// event store. The windowed debit totals are supplied as arguments exactly as the impure shell reads them
/// from the projection (ADR-PC-023, <c>AccountBalanceReader.GetWindowedAuthorizationHoldCentsAsync</c>): here
/// the decider stays a pure function of (rules, windowed totals, amount), so the boundary is exercised
/// deterministically. The full HTTP path over a real windowed-spend read is the Integration tier.
/// </para>
/// <para>
/// Every case runs with an ample balance and an amount under the EUR 5 000 per-transaction ceiling, so ONLY
/// the velocity gate can fire — the decline is unambiguously a velocity breach (`LIMIT_EXCEEDED`, per ADR-PC-037
/// §D6 which names it "LIMIT_EXCEEDED (velocity/transaction)"), and the refusal's Detail names WHICH window
/// overflowed.
/// </para>
/// </remarks>
public sealed class CurrentAccountVelocityTests
{
    // The disk-backed production loader (mirrors CurrentAccountOverdraftTests): the shipped ca_pt_standard
    // declares EUR 10 000/day (1 000 000 cents) and EUR 100 000/month (10 000 000 cents) velocity caps.
    private static readonly CurrentAccountProductConfigStore Store = new(configsDir: null);

    private static readonly Guid AccountId = Guid.NewGuid();
    private static readonly DateOnly ValueDate = new(2026, 3, 5);

    private static AccountPosition ActiveAccount(string productCode) =>
        AccountPosition.Empty with
        {
            AccountId = AccountId,
            ProductCode = productCode,
            Currency = "EUR",
            Lifecycle = AccountLifecycle.Active,
        };

    private static AuthorizationRules RulesFor(string productCode) =>
        Store.Resolve(productCode)?.ToAuthorizationRules() ?? CurrentAccountProductConfig.None;

    // Ample balance + a sub-per-transaction-cap amount, so only the velocity gate can refuse. The windowed
    // daily/monthly totals are the debits already authorized in each window BEFORE this attempt — what the
    // command shell reads from the projection and hands to the pure decider.
    private static DomainEvent Authorize(
        long amountCents, long windowedDailyDebitCents, long windowedMonthlyDebitCents,
        string productCode = "ca_pt_standard") =>
        CurrentAccountAuthorizeDecider.Decide(
            ActiveAccount(productCode),
            new AuthorizationRequest(AccountId, AccountId.ToString(), "hold-under-test", new Money(amountCents), ValueDate),
            availableBalanceCents: 100_000_000,
            RulesFor(productCode),
            activeFreeze: null,
            windowedDailyDebitCents,
            windowedMonthlyDebitCents);

    // --- the shipped config resolves its velocity caps ---

    [Fact]
    public void The_shipped_ca_pt_standard_config_resolves_its_daily_and_monthly_velocity_caps()
    {
        var config = Store.Resolve("ca_pt_standard");
        Assert.NotNull(config);
        Assert.Equal(1_000_000, config!.DailyVelocityLimitCents);    // EUR 10 000 / day
        Assert.Equal(10_000_000, config.MonthlyVelocityLimitCents);  // EUR 100 000 / month
    }

    [Fact]
    public void The_shipped_ca_pt_basic_config_declares_no_velocity_caps()
    {
        var config = Store.Resolve("ca_pt_basic");
        Assert.NotNull(config);
        Assert.Null(config!.DailyVelocityLimitCents);
        Assert.Null(config.MonthlyVelocityLimitCents);
    }

    // --- VELOCITY_LIMIT_PACK_BOUNDED: the daily window ---

    [Fact]
    public void VELOCITY_LIMIT_PACK_BOUNDED_a_debit_within_the_daily_cap_authorizes()
    {
        // EUR 5 000 already spent today, a EUR 4 000 debit → EUR 9 000 ≤ the EUR 10 000 daily cap → authorized.
        var result = Authorize(amountCents: 400_000, windowedDailyDebitCents: 500_000, windowedMonthlyDebitCents: 500_000);

        var hold = Assert.IsType<HoldPlaced>(result);
        Assert.Equal(400_000, hold.Amount.Cents);
    }

    [Fact]
    public void VELOCITY_LIMIT_PACK_BOUNDED_a_debit_breaching_the_daily_cap_is_refused_LIMIT_EXCEEDED()
    {
        // EUR 9 000 already spent today, a EUR 2 000 debit → EUR 11 000 > the EUR 10 000 daily cap → refused.
        // The amount is under the per-transaction cap and the balance is ample, so ONLY the daily velocity
        // gate fires; the decline names the window (DAILY_VELOCITY).
        var result = Authorize(amountCents: 200_000, windowedDailyDebitCents: 900_000, windowedMonthlyDebitCents: 900_000);

        var declined = Assert.IsType<AuthorizationDeclined>(result);
        Assert.Equal(AccountDeclinedReason.LimitExceeded, declined.DeclinedReason);
        Assert.Equal("DAILY_VELOCITY", declined.Detail);
    }

    [Fact]
    public void VELOCITY_LIMIT_PACK_BOUNDED_a_debit_exactly_at_the_daily_cap_authorizes_the_boundary_is_inclusive()
    {
        // EUR 9 000 spent + a EUR 1 000 debit = exactly the EUR 10 000 cap → authorized (the gate refuses
        // only a total ABOVE the cap, mirroring the per-transaction and overdraft boundaries).
        var result = Authorize(amountCents: 100_000, windowedDailyDebitCents: 900_000, windowedMonthlyDebitCents: 900_000);

        Assert.IsType<HoldPlaced>(result);
    }

    // --- VELOCITY_LIMIT_PACK_BOUNDED: the monthly window ---

    [Fact]
    public void VELOCITY_LIMIT_PACK_BOUNDED_a_debit_within_the_monthly_cap_authorizes()
    {
        // EUR 90 000 spent this month + a EUR 2 000 debit = EUR 92 000 ≤ the EUR 100 000 monthly cap, and
        // the day is clear → authorized.
        var result = Authorize(amountCents: 200_000, windowedDailyDebitCents: 0, windowedMonthlyDebitCents: 9_000_000);

        Assert.IsType<HoldPlaced>(result);
    }

    [Fact]
    public void VELOCITY_LIMIT_PACK_BOUNDED_a_debit_breaching_the_monthly_cap_is_refused_LIMIT_EXCEEDED()
    {
        // EUR 99 000 spent this month + a EUR 2 000 debit = EUR 101 000 > the EUR 100 000 monthly cap →
        // refused, even though the day is clear (EUR 2 000 is well under the EUR 10 000 daily cap). The
        // decline names the monthly window.
        var result = Authorize(amountCents: 200_000, windowedDailyDebitCents: 0, windowedMonthlyDebitCents: 9_900_000);

        var declined = Assert.IsType<AuthorizationDeclined>(result);
        Assert.Equal(AccountDeclinedReason.LimitExceeded, declined.DeclinedReason);
        Assert.Equal("MONTHLY_VELOCITY", declined.Detail);
    }

    // --- VELOCITY_LIMIT_PACK_BOUNDED: gate order and the unconstrained account ---

    [Fact]
    public void VELOCITY_LIMIT_PACK_BOUNDED_a_debit_breaching_both_windows_names_the_daily_window_first()
    {
        // Both the daily and the monthly totals would overflow; the daily gate is evaluated first, so the
        // decline names DAILY_VELOCITY — a stable, documented order.
        var result = Authorize(amountCents: 200_000, windowedDailyDebitCents: 900_000, windowedMonthlyDebitCents: 9_900_000);

        var declined = Assert.IsType<AuthorizationDeclined>(result);
        Assert.Equal(AccountDeclinedReason.LimitExceeded, declined.DeclinedReason);
        Assert.Equal("DAILY_VELOCITY", declined.Detail);
    }

    [Fact]
    public void VELOCITY_LIMIT_PACK_BOUNDED_a_ca_pt_basic_account_declaring_no_caps_is_unconstrained_by_velocity()
    {
        // ca_pt_basic sets no velocity caps, so however large the windowed totals are the velocity gate is
        // transparent — a within-balance debit authorizes.
        var result = Authorize(
            amountCents: 400_000, windowedDailyDebitCents: 999_999_999, windowedMonthlyDebitCents: 999_999_999,
            productCode: "ca_pt_basic");

        Assert.IsType<HoldPlaced>(result);
    }
}
