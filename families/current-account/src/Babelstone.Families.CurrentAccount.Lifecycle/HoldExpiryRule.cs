using Babelstone.Engine;
using Babelstone.Lifecycle;

namespace Babelstone.Families.CurrentAccount.Lifecycle;

/// <summary>
/// The current_account family's lifecycle-command rule (ADR-PC-037; ADR-PC-036) — the
/// projection-derived hold-expiry case of the driver's per-family <see cref="ILifecycleCommandRule"/> port.
/// In plain terms: the engine knows every authorization hold's value-date but owns no clock to expire it
/// (ADR-PC-023); this rule reads the spine active-hold set as-of a horizon and says "these holds are due to
/// expire, fire <c>expire_hold</c> on each", and the generic driver derives the canonical id, dedupes, and
/// POSTs <c>HoldExpired</c> to the engine command surface — the write-side realisation of the ADR-PC-037
/// projection-derived hold expiry.
/// </summary>
/// <remarks>
/// <para>
/// <b>Reads the SPINE, not a family read model.</b> Unlike <c>MaturityRule</c> (which range-scans the
/// family-owned deposit read model), a current account's holds are a spine-owned fold (account_holds,
/// ADR-PC-033 — the family tracks no holds on its own state), so the rule reads
/// <see cref="AccountBalanceReader.GetExpiryCandidatesAsync"/>: the ADR-PC-023 projection-derived read that
/// returns every ACTIVE authorization hold whose value_date is at/before the horizon, across all accounts.
/// The horizon is the <c>asOf</c> INPUT the worker supplies — never a clock read — so the fold
/// stays replay-deterministic.
/// </para>
/// <para>
/// <b>Per-hold occurrence, not one-shot.</b> A hold expiry is keyed on the placing event's per-stream
/// sequence (<see cref="Hold.PlacedSequence"/>), the stable long that makes each of an account's many holds
/// its own occurrence (ADR-PC-036), so returning a still-due hold on every pass fires it at most
/// once (the dispatch ledger + the engine's command_dedup both absorb the re-tick). The expiry read is
/// already state+kind precise (ACTIVE authorization holds only), so — unlike maturity's by-date-only deposit
/// scan — the rule needs no in-rule lifecycle re-filter.
/// </para>
/// </remarks>
public sealed class HoldExpiryRule(AccountBalanceReader balances) : ILifecycleCommandRule
{
    /// <summary>The STABLE command-kind the hold-expiry idempotency key is derived under — the shared dispatch
    /// mapping's <see cref="CurrentAccountHoldExpiryDispatch.CommandKindExpireHold"/>, re-exposed here for
    /// callers.</summary>
    public const string CommandKindExpireHold = CurrentAccountHoldExpiryDispatch.CommandKindExpireHold;

    private readonly AccountBalanceReader _balances =
        balances ?? throw new ArgumentNullException(nameof(balances));

    /// <inheritdoc />
    public string FamilyName => "current_account";

    /// <summary>
    /// Produce an <c>expire_hold</c> command for every ACTIVE authorization hold whose value-date is on or
    /// before <paramref name="asOf"/>. The driver's pass derives each decision's number-pinned id and dedupes
    /// it, so returning the same still-due hold on every pass fires it at most once (ADR-PC-036).
    /// </summary>
    public async Task<IReadOnlyList<LifecycleCommandDecision>> EvaluateAsync(
        DateOnly asOf, CancellationToken ct = default)
    {
        // The ADR-PC-023 projection-derived expiry-horizon read: ACTIVE authorization holds with value_date
        // at/before the horizon, across all accounts. asOf is the horizon INPUT (never a clock read).
        var candidates = await _balances.GetExpiryCandidatesAsync(asOf, ct);

        var decisions = new List<LifecycleCommandDecision>(candidates.Count);
        foreach (var hold in candidates)
        {
            // AccountRef is the account stream id as text (AccountPosition.AccountRef => AccountId.ToString());
            // parse it back to the Guid the InstanceId / idempotency-key derivation needs. A malformed ref
            // would be data corruption, so a throwing parse is the correct fail-loud, never a silent skip.
            var accountId = Guid.Parse(hold.AccountRef);

            // The ONE shared dispatch mapping (ADR-PC-036): OccurrenceKey is the placing event's
            // sequence (a stable long per hold), and value_date rides the body as the business valid_time. An
            // expiry candidate is an ACTIVE authorization hold by query, so its value_date is non-null.
            decisions.Add(CurrentAccountHoldExpiryDispatch.ExpireDecision(
                accountId, hold.HoldId, hold.PlacedSequence, hold.ValueDate!.Value));
        }

        return decisions;
    }
}
