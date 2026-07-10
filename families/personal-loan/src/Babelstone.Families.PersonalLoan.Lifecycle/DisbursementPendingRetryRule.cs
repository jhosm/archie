using Babelstone.Lifecycle;

namespace Babelstone.Families.PersonalLoan.Lifecycle;

/// <summary>
/// One loan held DISBURSEMENT-PENDING at source (ADR-PC-043 slot 5) — the
/// projection-derived facts the <see cref="DisbursementPendingRetryRule"/> needs to re-fire the held
/// disbursement: which loan, to which opaque destination, and its business start date. A structural,
/// no-PII shape (ADR-PC-004): opaque refs and an input date only.
/// </summary>
/// <param name="LoanId">The loan stream held disbursement-pending — the re-attempt's target and idempotency
/// instance id.</param>
/// <param name="DisbursementAccountRef">The opaque borrower account the disbursement must land on — a
/// reference the engine resolves internally, never PII.</param>
/// <param name="StartDate">The loan's own disbursement start date — rides as the business valid_time so a
/// late re-fire records the correct date (ADR-PC-002).</param>
public sealed record DisbursementPendingLoan(Guid LoanId, string DisbursementAccountRef, DateOnly StartDate);

/// <summary>
/// The projection-driven read the <see cref="DisbursementPendingRetryRule"/> consults for the loans held
/// disbursement-pending (ADR-PC-043 slot 5). The personal_loan read model
/// deliberately carries NO lifecycle column (see <c>LoanInstanceFilterResolver</c>), so the held-loan
/// population is a distinct projection read supplied to the rule rather than a column scan — but it stays a
/// PROJECTION read, never a clock (ADR-PC-023: the projection IS the temporal signal), so the rule remains a
/// deterministic function of its inputs and is trivially testable with a fake.
/// </summary>
public interface IDisbursementPendingReader
{
    /// <summary>Every loan currently held disbursement-pending, ordered by loan id (a deterministic, stable
    /// order). A projection read, never a clock read inside the rule.</summary>
    Task<IReadOnlyList<DisbursementPendingLoan>> ListDisbursementPendingAsync(CancellationToken ct = default);
}

/// <summary>
/// A projection-driven, clock-free predicate the <see cref="DisbursementPendingRetryRule"/> consults to
/// decide whether a held disbursement's destination is RECEIVABLE again (ADR-PC-043 slot 5).
/// In plain English: a loan whose disbursement could not land is held
/// disbursement-pending; the re-attempt must fire ONLY once the borrower account can actually receive money
/// again (re-opened, reactivated, or re-targeted). This port answers exactly that "can this account receive
/// a credit now?" question off a PROJECTION read — never a clock (ADR-PC-023: the projection IS the signal),
/// so the rule stays a deterministic function of its inputs and is trivially testable with a fake probe. The
/// loan-family twin of the deposit family's <c>IPayoutDestinationReceivability</c> (each family declares its
/// own port so the loan lifecycle assembly takes no dependency on the deposit lifecycle assembly).
/// </summary>
public interface IPayoutDestinationReceivability
{
    /// <summary>
    /// Is the opaque destination account <paramref name="beneficiaryAccountRef"/> receivable (no longer
    /// rejecting a credit) as-of <paramref name="asOf"/>? A projection-driven read, never a clock read inside
    /// the rule. Returns <see langword="true"/> when the re-attempt may fire, <see langword="false"/> while
    /// the destination still rejects.
    /// </summary>
    Task<bool> IsReceivableAsync(string beneficiaryAccountRef, DateOnly asOf, CancellationToken ct = default);
}

/// <summary>
/// The personal-loan family's re-attempt rule for a held disbursement (ADR-PC-043 slot 5)
/// — the disbursement-pending twin of the deposit family's <c>PayoutPendingRetryRule</c>.
/// In plain English: when a loan was approved but its disbursement had nowhere to land, the loan is held
/// disbursement-pending at source (the money is never disgorged); this rule watches for those held loans and
/// re-fires the disbursement the moment a live destination exists, so the borrower's money reaches them
/// exactly once. It is the same projection-driven, clock-free <see cref="ILifecycleCommandRule"/> shape as
/// the deposit rule: it reads the held-loan population as-of today (never a clock, ADR-PC-023) and returns a
/// re-attempt decision per still-held, now-receivable loan; the generic driver derives the number-pinned
/// dispatch id, dedupes it, and POSTs — so returning the same still-pending loan on every pass re-fires it at
/// most once.
/// </summary>
/// <remarks>
/// <para>
/// <b>Two projection reads, no clock — the one gate this rule owns.</b> A re-attempt fires only when BOTH
/// (a) the source's disbursement-pending flag reads true (the loan is in the held population the injected
/// <see cref="IDisbursementPendingReader"/> yields) AND (b) the destination is no longer rejecting (the
/// injected <see cref="IPayoutDestinationReceivability"/> probe answers <see langword="true"/> for the
/// disbursement account). Neither read touches a clock — the projection IS the temporal signal (ADR-PC-023
/// §6); the as-of date is supplied by the driver's clock-owning worker loop, never read inside the rule.
/// </para>
/// <para>
/// <b>Exactly-once by construction.</b> The re-attempt re-fires the disbursement endpoint under the SAME
/// one-shot occurrence key (<see cref="PersonalLoanLifecycleDispatch.DisbursementOccurrence"/>, via
/// <see cref="PersonalLoanLifecycleDispatch.DisbursementRetryDecision"/>), so the driver's dispatch ledger
/// and the engine's <c>command_dedup</c> — plus the ADR-PC-043 slot-4 intent key — collapse a late original
/// apply and this re-attempt to exactly ONE landing. The loan cannot be double-disbursed.
/// </para>
/// </remarks>
public sealed class DisbursementPendingRetryRule(
    IDisbursementPendingReader pending,
    IPayoutDestinationReceivability receivability) : ILifecycleCommandRule
{
    private readonly IDisbursementPendingReader _pending =
        pending ?? throw new ArgumentNullException(nameof(pending));

    private readonly IPayoutDestinationReceivability _receivability =
        receivability ?? throw new ArgumentNullException(nameof(receivability));

    /// <inheritdoc />
    public string FamilyName => "personal_loan";

    /// <summary>
    /// Produce a disbursement re-attempt command for every disbursement-pending loan whose destination is
    /// receivable again as-of <paramref name="asOf"/>. The driver derives each decision's number-pinned id
    /// and dedupes it, so returning the same still-pending loan on every pass re-fires it at most once
    /// (ADR-PC-043 slot 5). A loan whose destination still rejects is skipped this pass and re-checked next
    /// pass — its funds stay held at source, never disgorged.
    /// </summary>
    public async Task<IReadOnlyList<LifecycleCommandDecision>> EvaluateAsync(
        DateOnly asOf, CancellationToken ct = default)
    {
        // (a) The source-side disbursement-pending flag: every loan held at source because its disbursement
        // could not land. A projection read, never a clock (ADR-PC-023).
        var pending = await _pending.ListDisbursementPendingAsync(ct);

        var decisions = new List<LifecycleCommandDecision>();
        foreach (var loan in pending)
        {
            // (b) The destination-side gate: re-fire ONLY when the disbursement account is receivable again.
            // While the destination still rejects, the loan stays held at source (skipped this pass), never
            // disgorged.
            if (!await _receivability.IsReceivableAsync(loan.DisbursementAccountRef, asOf, ct))
            {
                continue;
            }

            // Re-fire the SAME disbursement occurrence, so the driver's dedupe + the engine's command_dedup +
            // the slot-4 intent key collapse a late original apply and this re-attempt to exactly one landing.
            decisions.Add(PersonalLoanLifecycleDispatch.DisbursementRetryDecision(
                loan.LoanId, loan.DisbursementAccountRef, loan.StartDate));
        }

        return decisions;
    }
}
