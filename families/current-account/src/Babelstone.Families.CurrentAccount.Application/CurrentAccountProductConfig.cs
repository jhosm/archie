using Babelstone.Engine;

namespace Babelstone.Families.CurrentAccount.Application;

/// <summary>
/// The current_account stage-4 authorization facts a product config declares (ADR-PC-037 §D5): the
/// arranged-overdraft headroom, the per-transaction ceiling, and the rolling daily/monthly velocity caps
/// the authorize decider reads. In plain English: the numbers the config says about "how far below zero may
/// this product go, how big may one debit be, and how much may be spent in a day / a month" — read at
/// authorization stage 4 (ADR-PC-030 §P3), never a price.
/// </summary>
/// <remarks>
/// <para>
/// Family-owned, resolved from the current_account product-config the family loads itself
/// (<see cref="CurrentAccountProductConfigStore"/>) — deliberately NOT the spine term-deposit
/// <c>ProductConfig</c> / <c>IProductConfigStore</c>, which is deposit-shaped (it requires interest_variant
/// + term_days) and would reject a current-account variant. The dependency arrow stays family→engine: the
/// family reads its own config and hands the resolved engine-owned <see cref="AuthorizationRules"/> to the
/// pure decider (ENGINE_FAMILY_AGNOSTIC — the engine names no current-account config).
/// </para>
/// <para>
/// <b>Velocity.</b> A product config may declare rolling daily/monthly velocity caps
/// (<see cref="DailyVelocityLimitCents"/> / <see cref="MonthlyVelocityLimitCents"/>); the authorize decider
/// enforces them at stage 4 by measuring the attempt against the account's windowed debit total — a
/// projection-derived read (ADR-PC-023) the command shell supplies, so the decider stays pure. A cap the
/// config omits is simply unconstrained (<c>null</c>).
/// </para>
/// </remarks>
/// <param name="ProductCode">The product code these facts describe (e.g. <c>ca_pt_standard</c>).</param>
/// <param name="ArrangedOverdraftLimitCents">The arranged-overdraft headroom in integer cents (<c>0</c> ⇒ no overdraft).</param>
/// <param name="PerTransactionLimitCents">The per-transaction ceiling in integer cents, or <c>null</c> ⇒ no ceiling.</param>
/// <param name="DailyVelocityLimitCents">The rolling daily debit cap in integer cents, or <c>null</c> ⇒ no daily cap.</param>
/// <param name="MonthlyVelocityLimitCents">The rolling monthly debit cap in integer cents, or <c>null</c> ⇒ no monthly cap.</param>
public sealed record CurrentAccountProductConfig(
    string ProductCode,
    long ArrangedOverdraftLimitCents,
    long? PerTransactionLimitCents,
    long? DailyVelocityLimitCents,
    long? MonthlyVelocityLimitCents)
{
    /// <summary>
    /// The zero-overdraft, no-ceiling degenerate: the rules for a product that declares no overdraft and
    /// no per-transaction cap (a <c>ca_pt_basic</c> account), and the safe fallback for a product code the
    /// store holds no config for. Equal to the <see cref="AuthorizationRules"/> default, so the decider
    /// applies only the plain balance gate — no headroom, no ceiling. Mirrors
    /// <c>PartialWithdrawalPolicy.Unrestricted</c>.
    /// </summary>
    public static AuthorizationRules None { get; } = new();

    /// <summary>
    /// Map this config onto the engine-owned stage-4 <see cref="AuthorizationRules"/> the pure decider
    /// applies (ADR-PC-037 §D5): the arranged overdraft, the per-transaction ceiling, and the daily/monthly
    /// velocity caps. Pure and total — no clock, no I/O — so the authorize decision stays deterministic
    /// (ADR-PC-010 §P5). The rule record carries only the caps; the windowed debit totals a velocity cap is
    /// measured against are read separately by the command shell and handed to the decider.
    /// </summary>
    public AuthorizationRules ToAuthorizationRules() =>
        new(ArrangedOverdraftLimitCents, PerTransactionLimitCents, DailyVelocityLimitCents, MonthlyVelocityLimitCents);
}
