using Babelstone.FinancialTypes;

namespace Babelstone.Engine;

/// <summary>
/// One authorization attempt against a transactional account (ADR-PC-030 stages 3–5): "may N cents
/// be spent from this account right now, and if so, earmark them." The impure command shell reads
/// the available-balance fold and the pack rules, then hands BOTH here — the decider itself is
/// pure (ADR-PC-034: a synchronous read-state-and-append decision on the ADR-PC-029 surface).
/// </summary>
/// <param name="InstanceId">The account-owning instance (stream) the resulting hold rides — a structural id, not PII.</param>
/// <param name="AccountRef">The opaque account being debited — never PII (ADR-PC-004).</param>
/// <param name="HoldId">The hold's lifecycle dedup/correlation key the caller supplies (ADR-PC-033 slot 4)
/// — deterministic per authorization, so a replayed attempt earmarks at most once.</param>
/// <param name="Amount">The attempted debit, integer-cents <see cref="Money"/> (ADR-PC-010).</param>
/// <param name="ValueDate">The economic date of the attempt — supplied by the command, never a clock read.</param>
public sealed record AuthorizationRequest(
    Guid InstanceId,
    string AccountRef,
    string HoldId,
    Money Amount,
    DateOnly ValueDate);

/// <summary>
/// The stage-4 rule inputs (ADR-PC-030: "within product rules / limits / overdraft") the pack
/// supplies. The pack OWNS these values (ADR-PC-033 slot 6); the grammar that expresses them is a
/// named-but-separate pack-grammar expansion, so until it lands the command shell resolves them
/// from its pack surface and hands the resolved cents here — the decider never reads a pack.
/// </summary>
/// <param name="OverdraftLimitCents">The authorized overdraft (*descoberto autorizado*), in cents at or
/// above zero: how far below zero the available balance may go. Zero — the default — means no overdraft.</param>
/// <param name="PerTransactionLimitCents">An optional per-transaction ceiling; null means no ceiling.</param>
public sealed record AuthorizationRules(
    long OverdraftLimitCents = 0,
    long? PerTransactionLimitCents = null);

/// <summary>
/// Why an authorization was declined — the GENERIC spine taxonomy (each reason is a stable machine
/// code, never PII). A transactional family's richer declined taxonomy layers on top in its own
/// command surface; the spine names only the outcomes its own stages produce.
/// </summary>
public enum AuthorizationDeclineReason
{
    /// <summary>The attempted amount was zero or negative — structurally not an authorization.</summary>
    NonPositiveAmount,

    /// <summary>Stage 4: the amount exceeds the pack's per-transaction ceiling.</summary>
    PerTransactionLimitExceeded,

    /// <summary>Stages 3–4: the available balance — net of active holds, plus any authorized
    /// overdraft — does not cover the amount.</summary>
    InsufficientAvailableBalance,
}

/// <summary>
/// The authorization decision: <see cref="Authorized"/> carries the <see cref="HoldPlaced"/> fact
/// the shell appends (stage 5 — the earmark IS the approval's record); <see cref="Declined"/>
/// carries the reason the caller returns as <c>declined</c>. The decider never appends a
/// <see cref="HoldPlaced"/> on a refusal (ADR-PC-033 slot 5) — what refusal FACT a family records
/// is that family's own contract, which is why the spine decision carries data, not a refusal event.
/// </summary>
public abstract record AuthorizationDecision
{
    private AuthorizationDecision() { }

    /// <summary>Approved: append <see cref="Hold"/> — from its sequence forward the earmark lowers
    /// the available balance, which is what makes the NEXT concurrent authorization safe without
    /// locking (ADR-PC-030 §48).</summary>
    public sealed record Authorized(HoldPlaced Hold) : AuthorizationDecision;

    /// <summary>Refused: nothing is earmarked; the caller answers <c>declined</c> with the reason.</summary>
    public sealed record Declined(AuthorizationDeclineReason Reason) : AuthorizationDecision;
}

/// <summary>
/// The funds-and-rules core of real-time authorization — the engine-owned stages 3–5 of the
/// ADR-PC-030 §P3 pipeline, as one pure decider. In plain English: given what is spendable right
/// now (the available-balance fold) and the pack's limit rules, either refuse the debit or produce
/// the <see cref="HoldPlaced"/> fact that earmarks the money. Everything impure — reading the fold,
/// resolving pack rules, the append itself, SCA, fraud, the rails — lives OUTSIDE (the excluded
/// stages are external by decision; the append is the shell's, idempotent on the command id per
/// ADR-PC-029).
/// </summary>
/// <remarks>
/// <para>
/// <b>The gate blocks the authorization, never the recording of facts (ADR-PC-033 slot 5).</b>
/// Only this decision consults the balance, BEFORE its append; the balance and hold folds
/// themselves are never gated. The <c>amount ≤ available</c> invariant is enforced HERE, at
/// authorization — the fold trusts its input stream, so prevention lives in the one place that
/// already reads state.
/// </para>
/// <para>
/// <b>Pure and family-agnostic (ADR-PC-010 / ADR-PC-021).</b> No clock, no I/O, no randomness —
/// the same inputs always yield the same decision, so the authorization path stays a deterministic
/// fold even though its answer gates the caller. It names no family: opaque refs, generic
/// <see cref="Money"/>, pack-supplied cents.
/// </para>
/// </remarks>
public static class FundsAndRulesDecider
{
    /// <summary>
    /// Decide one authorization attempt. <paramref name="availableBalanceCents"/> is the CURRENT
    /// available-balance fold (<c>AccountBalanceReader.GetAvailableBalanceCentsAsync</c>) — already
    /// net of every active hold, so earlier approvals are visible to this decision by construction.
    /// </summary>
    public static AuthorizationDecision Decide(
        AuthorizationRequest request, long availableBalanceCents, AuthorizationRules rules)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(rules);

        // Structural gate: a non-positive debit is not an authorization at all.
        if (request.Amount.Cents <= 0)
        {
            return new AuthorizationDecision.Declined(AuthorizationDeclineReason.NonPositiveAmount);
        }

        // Stage 4 — product rules/limits: the pack's per-transaction ceiling refuses before any
        // funds arithmetic (a rule breach is a rule breach regardless of balance).
        if (rules.PerTransactionLimitCents is { } limit && request.Amount.Cents > limit)
        {
            return new AuthorizationDecision.Declined(AuthorizationDeclineReason.PerTransactionLimitExceeded);
        }

        // Stages 3–4 — funds under pack rules: the available balance (already net of active holds)
        // may go down to −overdraft (*descoberto autorizado*) but no further.
        if (availableBalanceCents - request.Amount.Cents < -rules.OverdraftLimitCents)
        {
            return new AuthorizationDecision.Declined(AuthorizationDeclineReason.InsufficientAvailableBalance);
        }

        // Stage 5 — earmark: the approval IS the HoldPlaced fact. From its append forward the hold
        // lowers the available balance, so the next concurrent authorization already sees it.
        return new AuthorizationDecision.Authorized(new HoldPlaced(
            InstanceId: request.InstanceId,
            HoldId: request.HoldId,
            AccountRef: request.AccountRef,
            Amount: request.Amount,
            ValueDate: request.ValueDate));
    }
}
