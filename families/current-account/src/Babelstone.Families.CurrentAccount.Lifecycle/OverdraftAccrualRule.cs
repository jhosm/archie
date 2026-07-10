using Babelstone.Engine;
using Babelstone.Lifecycle;

namespace Babelstone.Families.CurrentAccount.Lifecycle;

/// <summary>
/// The current_account family's overdraft-interest accrual rule (ADR-PC-037 §D5; ADR-PC-036) — the
/// projection-derived accrual case of the driver's per-family <see cref="ILifecycleCommandRule"/> port. In
/// plain terms: the engine knows every account's drawn balance but owns no clock to charge daily interest
/// (ADR-PC-023); this rule reads the spine's "who is overdrawn?" set as-of a horizon and says "these accounts
/// are drawn, accrue today's overdraft interest on each", and the generic driver derives the canonical id,
/// dedupes, and POSTs the accrual to the engine command surface — the write-side realisation of the ADR-PC-037
/// projection-derived overdraft accrual.
/// </summary>
/// <remarks>
/// <para>
/// <b>Reads the SPINE, family-agnostic.</b> Like <see cref="HoldExpiryRule"/> (and unlike the deposit/loan
/// rules, which range-scan a family-owned read model), a current account's balance is a spine-owned fold
/// (movement_ledger, ADR-PC-033), so the rule reads <see cref="AccountBalanceReader.GetOverdrawnAccountsAsync"/>:
/// every account whose accounting balance is below zero, across all accounts. The set names no family; the
/// "which product accrues at what rate" policy is the command-side accrual shell's (it reads the account's
/// product config + the rate sheet), and a candidate that is not a drawn current account with an overdraft rate
/// is a harmless no-op at the endpoint. In this estate current accounts are the transactional accounts that
/// draw below zero (deposits/loans post against funding accounts, not their own), so the cross-family set is in
/// practice the current-account set; a future family that also overdraws would add a family filter here.
/// </para>
/// <para>
/// <b>Per-day occurrence, not one-shot.</b> Overdraft interest is a RECURRING daily charge, keyed on the
/// accrual day's ordinal (<see cref="CurrentAccountOverdraftAccrualDispatch"/>): returning a still-drawn
/// account on every pass fires at most one accrual per account per day (the dispatch ledger + the engine's
/// command_dedup both absorb the re-tick). The <c>asOf</c> horizon is the worker's INPUT — never a clock read —
/// so the decision stays replay-deterministic (ADR-PC-023 §6).
/// </para>
/// </remarks>
public sealed class OverdraftAccrualRule(AccountBalanceReader balances) : ILifecycleCommandRule
{
    /// <summary>The STABLE command-kind the accrual idempotency key is derived under — the shared dispatch
    /// mapping's <see cref="CurrentAccountOverdraftAccrualDispatch.CommandKindAccrueOverdraftInterest"/>,
    /// re-exposed here for callers.</summary>
    public const string CommandKindAccrueOverdraftInterest =
        CurrentAccountOverdraftAccrualDispatch.CommandKindAccrueOverdraftInterest;

    private readonly AccountBalanceReader _balances =
        balances ?? throw new ArgumentNullException(nameof(balances));

    /// <inheritdoc />
    public string FamilyName => "current_account";

    /// <summary>
    /// Produce an <c>accrue_overdraft_interest</c> command for every account drawn below zero as-of
    /// <paramref name="asOf"/>. The driver's pass derives each decision's number-pinned id (the day ordinal) and
    /// dedupes it, so returning the same still-drawn account on every pass accrues at most once per day (ADR-PC-036).
    /// </summary>
    public async Task<IReadOnlyList<LifecycleCommandDecision>> EvaluateAsync(
        DateOnly asOf, CancellationToken ct = default)
    {
        // The ADR-PC-032 read-side / ADR-PC-037 §D5 overdraft read: every account with a negative accounting
        // balance, across all accounts. asOf is the accrual-day INPUT (never a clock read).
        var overdrawn = await _balances.GetOverdrawnAccountsAsync(ct);

        var decisions = new List<LifecycleCommandDecision>(overdrawn.Count);
        foreach (var account in overdrawn)
        {
            // AccountRef is the account stream id as text (AccountPosition.AccountRef => AccountId.ToString());
            // parse it back to the Guid the InstanceId / idempotency-key derivation needs. The overdraft set is
            // family-agnostic, so a ref that is not a Guid is a non-current-account account_ref shape — SKIP it
            // (a defensive skip, not fail-loud: the current-account accrual endpoint is the product filter, and
            // one malformed cross-family ref must not crash the whole accrual pass).
            if (!Guid.TryParse(account.AccountRef, out var accountId))
            {
                continue;
            }

            decisions.Add(CurrentAccountOverdraftAccrualDispatch.AccrueDecision(accountId, asOf));
        }

        return decisions;
    }
}
