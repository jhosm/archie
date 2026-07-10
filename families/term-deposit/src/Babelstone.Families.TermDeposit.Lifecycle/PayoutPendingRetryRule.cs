using Babelstone.Families.TermDeposit;
using Babelstone.Lifecycle;

namespace Babelstone.Families.TermDeposit.Lifecycle;

/// <summary>
/// A projection-driven, clock-free predicate the <see cref="PayoutPendingRetryRule"/> consults to decide
/// whether a held payout's destination is RECEIVABLE again (ADR-PC-043 slot 5; bd babelstone-98mj.6). In
/// plain English: a matured deposit whose payout could not land is held payout-pending; the re-attempt must
/// fire ONLY once the beneficiary account can actually receive money again (re-opened, reactivated, or
/// re-targeted). This port answers exactly that "can this account receive a credit now?" question off a
/// PROJECTION read — never a clock, never a wall-time (ADR-PC-023: the projection IS the signal), so the rule
/// stays a deterministic function of its inputs and is trivially testable with a fake probe.
/// </summary>
public interface IPayoutDestinationReceivability
{
    /// <summary>
    /// Is the opaque beneficiary account <paramref name="beneficiaryAccountRef"/> receivable (no longer
    /// rejecting a credit) as-of <paramref name="asOf"/>? A projection-driven read (the CA's credit-admission
    /// predicate over its own read model), never a clock read inside the rule. Returns <see langword="true"/>
    /// when the re-attempt may fire, <see langword="false"/> while the destination still rejects.
    /// </summary>
    Task<bool> IsReceivableAsync(string beneficiaryAccountRef, DateOnly asOf, CancellationToken ct = default);
}

/// <summary>
/// The term-deposit family's re-attempt rule for a held maturity payout (ADR-PC-043 slot 5; bd
/// babelstone-98mj.6) — the payout-pending twin of the one-shot <see cref="MaturityRule"/>. In plain English:
/// when a deposit matured but its payout had nowhere to land, the deposit is held payout-pending at source
/// (the money is never disgorged); this rule watches for those held deposits and re-fires the payout the
/// moment a live destination exists, so the customer's money reaches them exactly once. It is the same
/// projection-driven, clock-free <see cref="ILifecycleCommandRule"/> shape as <see cref="MaturityRule"/>:
/// it reads the deposit read model as-of today (never a clock, ADR-PC-023) and returns a re-attempt decision
/// per still-held, now-receivable deposit; the generic driver derives the number-pinned dispatch id, dedupes
/// it, and POSTs — so returning the same still-pending deposit on every pass re-fires it at most once.
/// </summary>
/// <remarks>
/// <para>
/// <b>Two projection reads, no clock — the one gate this rule owns.</b> A re-attempt fires only when BOTH
/// (a) the source's payout-pending flag reads true (the deposit is <see cref="DepositLifecycle.PayoutPending"/>
/// in the read model) AND (b) the destination is no longer rejecting (the injected
/// <see cref="IPayoutDestinationReceivability"/> probe answers <see langword="true"/> for the beneficiary
/// account). Neither read touches a clock — the projection IS the temporal signal (ADR-PC-023 §6); the
/// as-of date is supplied by the driver's clock-owning worker loop, never read inside the rule.
/// </para>
/// <para>
/// <b>Exactly-once by construction.</b> The re-attempt re-fires the SAME maturity endpoint under the SAME
/// one-shot occurrence key (<see cref="TermDepositLifecycleDispatch.MaturityOccurrence"/>, via
/// <see cref="TermDepositLifecycleDispatch.PayoutRetryDecision"/>), so the driver's dispatch ledger and the
/// engine's <c>command_dedup</c> — plus the ADR-PC-043 slot-4 intent key derived from the same source id +
/// occurrence — collapse a late original apply and this re-attempt to exactly ONE landing. The re-attempt
/// cannot double-pay: it is the same economic occurrence, retried, not a new one.
/// </para>
/// </remarks>
public sealed class PayoutPendingRetryRule(
    IDepositReadModelStore deposits,
    IPayoutDestinationReceivability receivability) : ILifecycleCommandRule
{
    private readonly IDepositReadModelStore _deposits =
        deposits ?? throw new ArgumentNullException(nameof(deposits));

    private readonly IPayoutDestinationReceivability _receivability =
        receivability ?? throw new ArgumentNullException(nameof(receivability));

    /// <inheritdoc />
    public string FamilyName => "term_deposit";

    /// <summary>
    /// Produce a payout re-attempt command for every payout-pending deposit whose destination is receivable
    /// again as-of <paramref name="asOf"/>. The driver derives each decision's number-pinned id and dedupes
    /// it, so returning the same still-pending deposit on every pass re-fires it at most once (ADR-PC-043
    /// slot 5). A deposit whose destination still rejects is skipped this pass and re-checked next pass —
    /// its funds stay held at source, never disgorged.
    /// </summary>
    public async Task<IReadOnlyList<LifecycleCommandDecision>> EvaluateAsync(
        DateOnly asOf, CancellationToken ct = default)
    {
        // (a) The source-side payout-pending flag: every deposit held at source because its payout could not
        // land (the read model's PayoutPending lifecycle). A projection read, never a clock (ADR-PC-023).
        var pending = await _deposits.ListPayoutPendingAsync(ct);

        var decisions = new List<LifecycleCommandDecision>();
        foreach (var deposit in pending)
        {
            // (b) The destination-side gate: re-fire ONLY when the beneficiary account is receivable again.
            // The beneficiary account is the deposit's own stream (its degenerate account_ref, ADR-PC-033
            // slot 1) — the maturity payout lands the deposit's proceeds; a re-target changes the projection
            // the probe reads, not this rule. While the destination still rejects, the deposit stays held at
            // source (skipped this pass), never disgorged.
            if (!await _receivability.IsReceivableAsync(deposit.StreamId.ToString(), asOf, ct))
            {
                continue;
            }

            // Re-fire the SAME maturity occurrence (same kind/occurrence/path/body as the original), so the
            // driver's dedupe + the engine's command_dedup + the slot-4 intent key collapse a late original
            // apply and this re-attempt to exactly one landing (ADR-PC-043 §Idempotency).
            decisions.Add(TermDepositLifecycleDispatch.PayoutRetryDecision(deposit.StreamId, deposit.MaturityDate));
        }

        return decisions;
    }
}
