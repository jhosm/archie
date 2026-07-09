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
/// <param name="HoldId">The hold's lifecycle dedup/correlation key the caller supplies (ADR-PC-033)
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
/// supplies. The pack OWNS these values (ADR-PC-033); the command shell resolves them from its
/// pack surface and hands the resolved cents here — the decider never reads a pack.
/// </summary>
/// <param name="OverdraftLimitCents">The authorized overdraft (*descoberto autorizado*), in cents at or
/// above zero: how far below zero the available balance may go. Zero — the default — means no overdraft.</param>
/// <param name="PerTransactionLimitCents">An optional per-transaction ceiling; null means no ceiling.</param>
/// <param name="DailyVelocityLimitCents">An optional rolling DAILY debit cap: the sum of debits in the
/// day window plus this attempt may not exceed it (ADR-PC-037 §D5). Null means no daily cap. The window's
/// debit total is a projection-derived read the shell supplies (ADR-PC-023), never read here.</param>
/// <param name="MonthlyVelocityLimitCents">An optional rolling MONTHLY debit cap, evaluated exactly as
/// the daily one over the month window. Null means no monthly cap.</param>
public sealed record AuthorizationRules(
    long OverdraftLimitCents = 0,
    long? PerTransactionLimitCents = null,
    long? DailyVelocityLimitCents = null,
    long? MonthlyVelocityLimitCents = null);

/// <summary>
/// Why an authorization was declined — the GENERIC spine taxonomy (each reason is a stable machine
/// code, never PII). A transactional family's richer declined taxonomy layers on top in its own
/// command surface; the spine names only the outcomes its own stages produce.
/// </summary>
public enum AuthorizationDeclineReason
{
    /// <summary>The attempted amount was zero or negative — structurally not an authorization.</summary>
    NonPositiveAmount,

    /// <summary>Stage 3 (freeze gate, ADR-PC-041): the instance is under an active compliance freeze
    /// (an <c>AccountFrozen</c> with no matching <c>AccountUnfrozen</c>), so every debit is refused
    /// until it lifts — evaluated BEFORE the funds check, and the decline names the freeze
    /// reason/actor (<see cref="AuthorizationDecision.Declined"/>).</summary>
    AccountFrozen,

    /// <summary>Stage 4: the amount exceeds the pack's per-transaction ceiling.</summary>
    PerTransactionLimitExceeded,

    /// <summary>Stage 4: the debit would take the account's rolling DAILY debit total past the pack's
    /// daily velocity cap (ADR-PC-037 §D5). The window total is a projection-derived input the shell
    /// supplies; a family folds this back onto its own limit taxonomy (the spine stays generic).</summary>
    DailyVelocityLimitExceeded,

    /// <summary>Stage 4: the debit would take the account's rolling MONTHLY debit total past the pack's
    /// monthly velocity cap (ADR-PC-037 §D5) — the month-window counterpart of the daily cap.</summary>
    MonthlyVelocityLimitExceeded,

    /// <summary>Stages 3–4: the available balance — net of active holds (authorization AND legal,
    /// ADR-PC-041), plus any authorized overdraft — does not cover the amount.</summary>
    InsufficientAvailableBalance,
}

/// <summary>
/// The authorization decision: <see cref="Authorized"/> carries the <see cref="HoldPlaced"/> fact
/// the shell appends (stage 5 — the earmark IS the approval's record); <see cref="Declined"/>
/// carries the reason the caller returns as <c>declined</c>. The decider never appends a
/// <see cref="HoldPlaced"/> on a refusal (ADR-PC-033) — what refusal FACT a family records is that
/// family's own contract, which is why the spine decision carries data, not a refusal event.
/// </summary>
public abstract record AuthorizationDecision
{
    private AuthorizationDecision() { }

    /// <summary>Approved: append <see cref="Hold"/> — from its sequence forward the earmark is on
    /// the log, and once the spine projection drive folds it the available balance drops, which is
    /// what makes a later authorization safe without locking (ADR-PC-030). The command shell owns
    /// read-your-writes: it drains before it decides, so "later" never trusts a stale fold.</summary>
    public sealed record Authorized(HoldPlaced Hold) : AuthorizationDecision;

    /// <summary>
    /// Refused: nothing is earmarked; the caller answers <c>declined</c> with the reason. When
    /// <see cref="Reason"/> is <see cref="AuthorizationDeclineReason.AccountFrozen"/> the decline
    /// NAMES the freeze (ADR-PC-041 slot 5): <see cref="FreezeReason"/>/<see cref="ComplianceActor"/>
    /// carry the machine-code reason and the operator that placed it, so "why was this refused?" is a
    /// read, not a forensic log dig (HOLD_REASON_OBSERVABLE). Both are null for every other reason.
    /// </summary>
    public sealed record Declined(
        AuthorizationDeclineReason Reason,
        string? FreezeReason = null,
        string? ComplianceActor = null) : AuthorizationDecision;
}

/// <summary>
/// The funds-and-rules core of real-time authorization — the engine-owned stages 3–5 of the
/// ADR-PC-030 authorization pipeline, as one pure decider. In plain English: given what is spendable right
/// now (the available-balance fold) and the pack's limit rules, either refuse the debit or produce
/// the <see cref="HoldPlaced"/> fact that earmarks the money. Everything impure — reading the fold,
/// resolving pack rules, the append itself, SCA, fraud, the rails — lives OUTSIDE (the excluded
/// stages are external by decision; the append is the shell's, idempotent on the command id per
/// ADR-PC-029).
/// </summary>
/// <remarks>
/// <para>
/// <b>The gate blocks the authorization, never the recording of facts (ADR-PC-033).</b>
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
    /// available-balance fold (<c>AccountBalanceReader.GetAvailableBalanceCentsAsync</c>) — net of
    /// every hold the spine projection drive has folded. Earlier approvals are visible once
    /// drained; the command shell drains before it decides (read-your-writes).
    /// </summary>
    /// <param name="request">The authorization attempt.</param>
    /// <param name="availableBalanceCents">The current available-balance fold, net of every active
    /// hold (authorization AND legal, ADR-PC-041) the spine projection drive has folded.</param>
    /// <param name="rules">The pack-supplied stage-4 rule inputs.</param>
    /// <param name="activeFreeze">The instance's active compliance freeze, or null if it is not
    /// frozen (ADR-PC-041) — read by the shell from <see cref="AccountFreezeReader"/> and handed in so
    /// the decider stays pure. When non-null, every debit is refused, naming the freeze.</param>
    /// <param name="windowedDailyDebitCents">The account's rolling DAILY debit total BEFORE this attempt
    /// — a projection-derived read (ADR-PC-023) the shell supplies over its chosen day window, never a
    /// clock read here. Only consulted when <see cref="AuthorizationRules.DailyVelocityLimitCents"/> is
    /// set; zero (the default) leaves the daily velocity gate transparent.</param>
    /// <param name="windowedMonthlyDebitCents">The account's rolling MONTHLY debit total BEFORE this
    /// attempt — the month-window counterpart of <paramref name="windowedDailyDebitCents"/>.</param>
    public static AuthorizationDecision Decide(
        AuthorizationRequest request, long availableBalanceCents, AuthorizationRules rules,
        AccountFreeze? activeFreeze = null,
        long windowedDailyDebitCents = 0, long windowedMonthlyDebitCents = 0)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(rules);

        // Structural gate: a non-positive debit is not an authorization at all.
        if (request.Amount.Cents <= 0)
        {
            return new AuthorizationDecision.Declined(AuthorizationDeclineReason.NonPositiveAmount);
        }

        // Stage 3 — freeze gate (ADR-PC-041 slot 5): an active compliance freeze refuses EVERY debit,
        // evaluated before the funds check, and the decline names the freeze reason/actor. A pure
        // read-state-and-decide step (the shell read the predicate; no clock, no I/O here) — the
        // freeze blocks the authorization, never the recording or folding of facts.
        if (activeFreeze is not null)
        {
            return new AuthorizationDecision.Declined(
                AuthorizationDeclineReason.AccountFrozen,
                FreezeReason: activeFreeze.FreezeReason,
                ComplianceActor: activeFreeze.ComplianceActor);
        }

        // Stage 4 — product rules/limits: the pack's per-transaction ceiling refuses before any
        // funds arithmetic (a rule breach is a rule breach regardless of balance).
        if (rules.PerTransactionLimitCents is { } limit && request.Amount.Cents > limit)
        {
            return new AuthorizationDecision.Declined(AuthorizationDeclineReason.PerTransactionLimitExceeded);
        }

        // Stage 4 — velocity: the pack's rolling daily/monthly debit caps (ADR-PC-037 §D5). The window
        // totals are projection-derived inputs the shell already read (ADR-PC-023); this attempt PUSHES
        // the window, so the breach test is windowedTotal + amount > cap. Integer cents throughout — no
        // rounding (ADR-PC-010). Daily is checked before monthly only for a stable decline order; both
        // are rule breaches evaluated before the funds check, like the per-transaction ceiling above.
        if (rules.DailyVelocityLimitCents is { } dailyCap && windowedDailyDebitCents + request.Amount.Cents > dailyCap)
        {
            return new AuthorizationDecision.Declined(AuthorizationDeclineReason.DailyVelocityLimitExceeded);
        }

        if (rules.MonthlyVelocityLimitCents is { } monthlyCap && windowedMonthlyDebitCents + request.Amount.Cents > monthlyCap)
        {
            return new AuthorizationDecision.Declined(AuthorizationDeclineReason.MonthlyVelocityLimitExceeded);
        }

        // Stages 3–4 — funds under pack rules: the available balance (already net of active holds)
        // may go down to −overdraft (*descoberto autorizado*) but no further.
        if (availableBalanceCents - request.Amount.Cents < -rules.OverdraftLimitCents)
        {
            return new AuthorizationDecision.Declined(AuthorizationDeclineReason.InsufficientAvailableBalance);
        }

        // Stage 5 — earmark: the approval IS the HoldPlaced fact. Once the appended hold is
        // drained into the read model it lowers the available balance the next decision reads.
        return new AuthorizationDecision.Authorized(new HoldPlaced(
            InstanceId: request.InstanceId,
            HoldId: request.HoldId,
            AccountRef: request.AccountRef,
            Amount: request.Amount,
            ValueDate: request.ValueDate));
    }
}
