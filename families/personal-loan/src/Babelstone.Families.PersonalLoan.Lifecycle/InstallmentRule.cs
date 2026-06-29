using Babelstone.Engine;
using Babelstone.Families.PersonalLoan;
using Babelstone.Lifecycle;

namespace Babelstone.Families.PersonalLoan.Lifecycle;

/// <summary>
/// The personal-loan family's lifecycle-command rule (ADR-PC-036 §Decision 2, 3 &amp; 5; bd babelstone-6cpq.9) —
/// the RECURRING installment case of the driver's per-family <see cref="ILifecycleCommandRule"/> port. In plain
/// terms: a loan owes one installment a month, and the engine owns no clock to collect them on their due dates
/// (ADR-PC-023); this rule reads the loan's forward <c>installment_calendar</c> read model as-of today, finds
/// the single NEXT-unpaid installment that has fallen due per Active loan, and says "fire <c>PayInstallment</c>
/// on it" — the generic driver derives the canonical id, dedupes, and POSTs. It is the recurring sibling of the
/// one-shot <c>MaturityRule</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>ALL safety rests on the number-pinned, server-derived idempotency key</b> (LCD-1, ADR-PC-036 §Decision 3).
/// <c>PayInstallment</c> is legal repeatedly from <c>Active</c>, so the engine's lifecycle legality gate gives a
/// repeat NO backstop — only a deterministic key hitting <c>command_dedup</c> stops a double-collection. The
/// occurrence key is the STABLE installment NUMBER (<see cref="InstallmentCalendarReadModelRow.NextInstallmentNumber"/>),
/// never the due-date, so the id the driver presents is exactly the one the engine derives server-side
/// (<c>LoansEndpoints.PayInstallmentCommandKind</c>, the next-unpaid number off the live fold). A re-tick or a
/// re-dated/backfilled retry of occurrence N therefore re-derives the SAME id and appends ONE money leg, never
/// two. The driver supplies the key derivation (the occurrence number) — it is not caller input.
/// </para>
/// <para>
/// <b>Advances to N+1 only once N is recorded paid.</b> The forward pointer is the calendar fold's next-unpaid
/// occurrence (<c>InstallmentsPaid + 1</c>), which advances only on the <c>LoanInstallmentPaid</c> event — so
/// the rule cannot surface N+1 until N's event lands, and it keeps re-presenting N (deduped) until then. The
/// scan is the half-open window <c>[DateOnly.MinValue, asOf + 1)</c> on the next due-date: it fires an
/// installment due on/before today (today inclusive), backfills an overdue one missed during an outage, and
/// excludes one not yet due. The store's range scan already excludes a terminal or fully-paid loan (its
/// forward pointer is NULL), so every returned row is an Active loan still owing an installment.
/// </para>
/// <para>
/// This rule does NOT encode the settlement-health gate (ADR-PC-036 §Decision 4 / LCD-2 — fire N+1 only when
/// N's de-settled cash leg is not parked): that is a separate recurring-driver concern, out of scope here
/// (this issue covers §Decisions 2, 3 &amp; 5). The collection account the installment debits is the loan's own
/// disbursement-account reference, recovered from the read-model row's structural detail body; it is an opaque
/// token, never an IBAN, and carries no PII (ADR-PC-004 §P2).
/// </para>
/// </remarks>
public sealed class InstallmentRule(IInstallmentCalendarReadModelStore loans) : ILifecycleCommandRule
{
    /// <summary>The STABLE command-kind the installment idempotency key is derived under. MUST equal the engine
    /// installment endpoint's own derivation kind (<c>LoansEndpoints.PayInstallmentCommandKind =
    /// "pay_installment"</c>) so the driver-derived id and the engine-derived id are identical (LCD-1,
    /// ADR-PC-036 §Decision 1+3) — named here, not referenced, to keep the driver free of a family-application
    /// compile dependency.</summary>
    public const string CommandKindPayInstallment = "pay_installment";

    // The loan's structural state (a LoanPosition) is serialized into the read-model row's Detail by the SAME
    // codec the read-model runner uses, so deserializing it here recovers the loan's disbursement-account
    // reference — the only per-loan account token the driver can present as the installment's collection
    // account. Pure (no clock, no I/O); a re-hydration of already-projected bytes.
    private static readonly JsonStateSerializer<LoanPosition> DetailSerializer = new();

    private readonly IInstallmentCalendarReadModelStore _loans =
        loans ?? throw new ArgumentNullException(nameof(loans));

    /// <inheritdoc />
    public string FamilyName => "personal_loan";

    /// <summary>
    /// Produce a <c>PayInstallment</c> command for every Active loan whose next-unpaid installment is due on or
    /// before <paramref name="asOf"/>. The driver's pass derives each decision's number-pinned id and dedupes
    /// it, so re-presenting the same still-due occurrence on every pass collects it at most once
    /// (ADR-PC-036 §Decision 2/3).
    /// </summary>
    public async Task<IReadOnlyList<LifecycleCommandDecision>> EvaluateAsync(
        DateOnly asOf, CancellationToken ct = default)
    {
        // Half-open [MinValue, asOf + 1): every loan whose next-unpaid installment is due on/before today
        // (today inclusive, tomorrow excluded), with no lower bound so an installment overdue from an outage
        // is still caught (backfill). The store excludes terminal/fully-paid loans (NULL next_due_date).
        var due = await _loans.ListByDueDateAsync(DateOnly.MinValue, asOf.AddDays(1), ct);

        var decisions = new List<LifecycleCommandDecision>();
        foreach (var loan in due)
        {
            // The range scan only surfaces rows with a present forward pointer (Active, installments
            // remaining); guard defensively so a NULL pair can never produce a numberless decision.
            if (loan.NextInstallmentNumber is not { } installmentNumber || loan.NextDueDate is not { } dueDate)
            {
                continue;
            }

            var collectionAccountRef = DetailSerializer.Deserialize(loan.Detail).DisbursementAccountRef;

            decisions.Add(new LifecycleCommandDecision(
                InstanceId: loan.StreamId,
                // The occurrence key is the stable installment NUMBER, never the due-date — the number-pin the
                // whole double-collection safety rests on (ADR-PC-036 §Decision 3, LCD-1).
                CommandKind: CommandKindPayInstallment,
                OccurrenceKey: installmentNumber,
                RequestPath: $"/v1/loans/{loan.StreamId:D}/installment",
                // paid_at carries the installment's OWN due date as the business valid_time (ADR-PC-036
                // §Context; ADR-PC-002). collection_account_ref is the loan's opaque account token; money is
                // cents-native and no PII rides the body (ADR-PC-004 §P2).
                Body: new Dictionary<string, object?>
                {
                    ["collection_account_ref"] = collectionAccountRef,
                    ["paid_at"] = AtUtcMidnight(dueDate),
                },
                DueAt: dueDate,
                // The loan installment endpoint derives its key server-side and is NOT SCA-step-up-gated, so
                // no scoped principal is presented (ADR-PC-036 §Decision 1; ILifecycleCommandDecision).
                ServicePrincipalScope: null));
        }

        return decisions;
    }

    // The due date rides as a value (ADR-PC-036 §Context): a DateOnly due date as UTC midnight, the wire shape
    // the engine endpoint's DateTimeOffset? PaidAt binds and stamps the event's valid_time from.
    private static DateTimeOffset AtUtcMidnight(DateOnly date) =>
        new(date.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
}
