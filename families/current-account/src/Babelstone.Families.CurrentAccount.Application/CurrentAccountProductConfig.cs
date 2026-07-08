using Babelstone.Engine;

namespace Babelstone.Families.CurrentAccount.Application;

/// <summary>
/// The current_account stage-4 authorization facts a product config declares (ADR-PC-037 §D5): the
/// arranged-overdraft headroom and the per-transaction ceiling the authorize decider reads. In plain
/// English: the two numbers the config says about "how far below zero may this product go, and how big
/// may one debit be" — read at authorization stage 4 (ADR-PC-030 §P3), never a price.
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
/// <b>Velocity scope-out.</b> A product config may declare daily/monthly velocity caps, but the engine
/// enforces only the per-transaction ceiling at stage 4 today — daily/monthly velocity needs a
/// windowed-spend projection (a rolling sum of debits) the engine does not yet carry, so those values are
/// read as declarative-only and are NOT mapped onto <see cref="AuthorizationRules"/> (a documented
/// scope-out, tracked as a follow-up).
/// </para>
/// </remarks>
/// <param name="ProductCode">The product code these facts describe (e.g. <c>ca_pt_standard</c>).</param>
/// <param name="ArrangedOverdraftLimitCents">The arranged-overdraft headroom in integer cents (<c>0</c> ⇒ no overdraft).</param>
/// <param name="PerTransactionLimitCents">The per-transaction ceiling in integer cents, or <c>null</c> ⇒ no ceiling.</param>
public sealed record CurrentAccountProductConfig(
    string ProductCode,
    long ArrangedOverdraftLimitCents,
    long? PerTransactionLimitCents)
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
    /// applies (ADR-PC-037 §D5). Pure and total — no clock, no I/O — so the authorize decision stays
    /// deterministic (ADR-PC-010 §P5). Only the arranged overdraft and per-transaction ceiling map;
    /// velocity is scoped out (see the type remarks).
    /// </summary>
    public AuthorizationRules ToAuthorizationRules() =>
        new(ArrangedOverdraftLimitCents, PerTransactionLimitCents);
}
