using Babelstone.Engine;
using Babelstone.Families.CurrentAccount;

namespace Babelstone.Families.CurrentAccount.Application;

/// <summary>
/// The pure decision core of the current_account synchronous AUTHORIZE command (ADR-PC-037 §D6 /
/// ADR-PC-034): given the account's folded <see cref="AccountPosition"/>, the read available balance,
/// the pack rules, and any active compliance freeze, it produces the ONE event the shell appends — an
/// <c>operations.HoldPlaced</c> earmark (authorized) or a family <see cref="AuthorizationDeclined"/>
/// refusal fact (declined). In plain English: this is the "can this debit go through?" brain — it reads
/// state and answers, but does no I/O and touches no clock, so it is unit-tested Docker-free.
/// </summary>
/// <remarks>
/// <para>
/// <b>It composes the engine spine, it does not reimplement it.</b> The funds/limit/freeze arithmetic is
/// the engine-owned family-agnostic <see cref="FundsAndRulesDecider"/> (ADR-PC-030 stages 3–5); this
/// decider adds only the two things the spine deliberately does not know: the account LIFECYCLE gate
/// (the spine never reads <see cref="AccountLifecycle"/>) and the RICHER declined taxonomy (ADR-PC-037
/// §D6). The spine's four generic reasons map onto the family's four product codes
/// (<see cref="AccountDeclinedReason"/>): the spine folds "no funds" and "beyond arranged overdraft" into
/// one <see cref="AuthorizationDeclineReason.InsufficientAvailableBalance"/>, so the split back into
/// <c>INSUFFICIENT_AVAILABLE_BALANCE</c> vs <c>OVERDRAFT_LIMIT_EXCEEDED</c> is the family's to make —
/// keeping product vocabulary out of the engine (ENGINE_FAMILY_AGNOSTIC).
/// </para>
/// <para>
/// <b>The refusal is an appended fact, not returned data (ADR-PC-033 slot 5 / ADR-PC-037 §D6).</b> The
/// spine decider returns declined DATA; recording the refusal is this family's obligation, so on a
/// decline this decider returns the <see cref="AuthorizationDeclined"/> event the shell appends — a
/// decline is an auditable event, never a silent non-append. Pure — no clock, no I/O, no randomness
/// (BENG001/002/003) — so the whole authorize path stays a deterministic, replayable decision.
/// </para>
/// </remarks>
public static class CurrentAccountAuthorizeDecider
{
    /// <summary>
    /// Decide one authorize attempt. Returns the event to append: an <see cref="HoldPlaced"/> (the debit
    /// is authorized and its funds earmarked) or an <see cref="AuthorizationDeclined"/> (the debit is
    /// refused, carrying a bounded <see cref="AccountDeclinedReason"/> code). The caller supplies the
    /// current available balance (drained before deciding, read-your-writes), the pack-resolved rules,
    /// and any active freeze — the decider reads none of them itself, so it stays pure.
    /// </summary>
    /// <param name="position">The account's folded lifecycle position (the source of the ACCOUNT_NOT_ACTIVE gate).</param>
    /// <param name="request">The authorization attempt (opaque account_ref, integer-cents amount, value-date, hold id).</param>
    /// <param name="availableBalanceCents">The current available-balance fold (accounting − Σ active holds), read before deciding.</param>
    /// <param name="rules">The pack-supplied stage-4 rule inputs (arranged overdraft, per-transaction limit).</param>
    /// <param name="activeFreeze">The account's active compliance freeze, or null when it is not frozen (ADR-PC-041).</param>
    public static DomainEvent Decide(
        AccountPosition position,
        AuthorizationRequest request,
        long availableBalanceCents,
        AuthorizationRules rules,
        AccountFreeze? activeFreeze)
    {
        ArgumentNullException.ThrowIfNull(position);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(rules);

        // Family lifecycle gate (ADR-PC-037 §D2 / §D6): every operating transition — authorize included —
        // is legal only from Active. A dormant / closed / failed / erased account cannot authorize a
        // debit, and the spine decider never reads lifecycle, so this gate is the family's. It runs
        // BEFORE the funds/limit arithmetic: an inactive account is refused regardless of its balance,
        // and the Detail names the blocking state (a stable code, never PII) for the audit trail.
        if (position.Lifecycle != AccountLifecycle.Active)
        {
            return Declined(position, request, AccountDeclinedReason.AccountNotActive, position.Lifecycle.ToString());
        }

        // Stages 3–5: the engine-owned funds/limit/freeze decider (ADR-PC-030). It answers Authorized
        // (with the HoldPlaced earmark) or Declined (with a generic reason) — nothing appended.
        var decision = FundsAndRulesDecider.Decide(request, availableBalanceCents, rules, activeFreeze);

        return decision switch
        {
            // Approved: append the earmark the spine produced (stage 5 — the HoldPlaced IS the approval record).
            AuthorizationDecision.Authorized authorized => authorized.Hold,

            // Refused: turn the spine's generic reason into the family's D6 code + refusal fact.
            AuthorizationDecision.Declined declined => Declined(
                position, request, MapReason(declined.Reason, rules), FreezeDetail(declined)),

            _ => throw new InvalidOperationException(
                $"Unexpected authorization decision '{decision.GetType().Name}'."),
        };
    }

    // Map the engine's generic decline reason onto the ADR-PC-037 §D6 family taxonomy. The spine is
    // narrower than the family on purpose (ENGINE_FAMILY_AGNOSTIC): it has no ACCOUNT_NOT_ACTIVE (a
    // lifecycle concept, gated above) and folds overdraft into insufficient-balance, so the family
    // re-splits them. A compliance freeze surfaces as ACCOUNT_NOT_ACTIVE (D6 groups "blocked" there).
    private static string MapReason(AuthorizationDeclineReason reason, AuthorizationRules rules) => reason switch
    {
        // A frozen account is "blocked" — one of the ACCOUNT_NOT_ACTIVE cases (ADR-PC-037 §D6).
        AuthorizationDeclineReason.AccountFrozen => AccountDeclinedReason.AccountNotActive,

        // The per-transaction ceiling (the only velocity cap the pure decider enforces; daily/monthly
        // velocity needs a windowed-spend read and lands with the pack-rule read).
        AuthorizationDeclineReason.PerTransactionLimitExceeded => AccountDeclinedReason.LimitExceeded,

        // The spine's single insufficient-funds reason is two product outcomes: if an arranged overdraft
        // was configured, the debit went BEYOND it (unarranged overdraft / ultrapassagem, ADR-PC-037 §D5);
        // with no overdraft configured, it is a plain shortfall. Until the arranged-overdraft pack-value
        // read lands (ARRANGED_OVERDRAFT_PACK_BOUNDED, Planned) the resolved overdraft is zero, so the
        // OVERDRAFT_LIMIT_EXCEEDED arm is exercised only by unit tests that pass an explicit overdraft.
        AuthorizationDeclineReason.InsufficientAvailableBalance => rules.OverdraftLimitCents > 0
            ? AccountDeclinedReason.OverdraftLimitExceeded
            : AccountDeclinedReason.InsufficientAvailableBalance,

        // The shell rejects a non-positive amount as a 400 before deciding, so the spine never returns
        // this on the authorize path — reaching it is an invariant breach, not a business decline.
        AuthorizationDeclineReason.NonPositiveAmount => throw new InvalidOperationException(
            "NonPositiveAmount reached the family decider; the authorize shell must reject a non-positive amount first."),

        _ => throw new InvalidOperationException($"Unmapped authorization decline reason '{reason}'."),
    };

    // Name a compliance-freeze refusal (HOLD_REASON_OBSERVABLE): the freeze reason code makes "why was
    // this refused?" a read, not a forensic dig. A stable code, never PII (ADR-PC-041). Null for every
    // non-freeze decline (the code stands alone).
    private static string? FreezeDetail(AuthorizationDecision.Declined declined) =>
        declined.Reason == AuthorizationDeclineReason.AccountFrozen ? declined.FreezeReason : null;

    private static AuthorizationDeclined Declined(
        AccountPosition position, AuthorizationRequest request, string reasonCode, string? detail) =>
        new(position.AccountId, reasonCode, request.Amount, request.ValueDate, detail);
}
