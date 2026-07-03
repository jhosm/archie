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
/// <b>The settlement-health gate bounds catch-up (ADR-PC-036 §Decision 4 / LCD-2,
/// <c>LIFECYCLE_DRIVER_SETTLEMENT_HEALTH_GATE</c>).</b> The paid-count advances on the installment EVENT,
/// not on settled CASH — the cash leg is the downstream ADR-PC-032 Originated <c>Movement</c>, effected by
/// the substrate-owned <c>SettlementProcess</c> saga. So before surfacing a loan's next-due occurrence, this
/// rule consults the <see cref="ISettlementHealthProbe"/> and REFUSES while the loan's cash leg is parked in
/// <c>HUMAN_INTERVENTION_REQUIRED</c>: N+1 is held while occurrence N's collection is stuck awaiting an
/// operator (and installment 1 is held while the disbursement's own leg is parked — strictly safer, same
/// predicate), so an automated catch-up after an outage never advances the paid-count past collected cash.
/// The rule re-evaluates every pass, so the schedule RESUMES on the first tick after the leg settles
/// (the operator's resolution drives the saga to <c>SETTLEMENT_COMPLETED</c>). A permanently parked leg
/// therefore stalls the loan's schedule BY DESIGN — and each hold is counted
/// (<c>LifecycleDriverMetrics.RecordScheduleHeld</c>, the <c>lifecycle_schedule_held_total</c> series), so
/// the stall is a metric, never invisible; only the alert RULE reading that series remains an ops
/// follow-up (ADR-PC-036 §Residual risks). Maturity (one-shot) needs no such gate — <c>MaturityRule</c>
/// takes no probe.
/// </para>
/// <para>
/// The collection account the installment debits is the loan's own disbursement-account reference, recovered
/// from the read-model row's structural detail body; it is an opaque token, never an IBAN, and carries no
/// PII (ADR-PC-004 §P2).
/// </para>
/// </remarks>
public sealed class InstallmentRule(
    IInstallmentCalendarReadModelStore loans,
    ISettlementHealthProbe settlementHealth) : ILifecycleCommandRule
{
    /// <summary>The STABLE command-kind the installment idempotency key is derived under — the shared
    /// dispatch mapping's <see cref="PersonalLoanLifecycleDispatch.CommandKindPayInstallment"/>, re-exposed
    /// here for existing callers.</summary>
    public const string CommandKindPayInstallment = PersonalLoanLifecycleDispatch.CommandKindPayInstallment;

    /// <summary>The scoped, non-interactive SCA service principal the loan installment money-mover route
    /// authorises the driver by — the shared dispatch mapping's
    /// <see cref="PersonalLoanLifecycleDispatch.MoneyMoverScope"/>, re-exposed here for existing callers.</summary>
    public const string DepositMoneyMoverScope = PersonalLoanLifecycleDispatch.MoneyMoverScope;

    // The loan's structural state (a LoanPosition) is serialized into the read-model row's Detail by the SAME
    // codec the read-model runner uses, so deserializing it here recovers the loan's disbursement-account
    // reference — the only per-loan account token the driver can present as the installment's collection
    // account. Pure (no clock, no I/O); a re-hydration of already-projected bytes.
    private static readonly JsonStateSerializer<LoanPosition> DetailSerializer = new();

    private readonly IInstallmentCalendarReadModelStore _loans =
        loans ?? throw new ArgumentNullException(nameof(loans));

    private readonly ISettlementHealthProbe _settlementHealth =
        settlementHealth ?? throw new ArgumentNullException(nameof(settlementHealth));

    /// <inheritdoc />
    public string FamilyName => "personal_loan";

    /// <summary>
    /// Produce a <c>PayInstallment</c> command for every Active loan whose next-unpaid installment is due on or
    /// before <paramref name="asOf"/> AND whose cash leg is not parked in human intervention (the LCD-2
    /// settlement-health gate, ADR-PC-036 §Decision 4 — a held loan is simply not surfaced this pass, and is
    /// re-evaluated next tick). The driver's pass derives each decision's number-pinned id and dedupes
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

            // The LCD-2 settlement-health gate (ADR-PC-036 §Decision 4): refuse to surface this loan's
            // next occurrence while its de-settled cash leg (ADR-PC-032 Originated Movement — the previous
            // installment's collection, or the disbursement itself) is parked in
            // HUMAN_INTERVENTION_REQUIRED. Held, not dropped: the loan is re-evaluated every pass and
            // resumes on the first tick after the operator resolves the leg — so a catch-up after an
            // outage never advances the paid-count past collected cash. A probe failure propagates
            // (backpressure, fail-closed) rather than guessing healthy.
            if (await _settlementHealth.IsParkedAsync(loan.StreamId, ct))
            {
                // Count the hold (lifecycle_schedule_held_total, once per held occurrence per pass) so
                // the stalled schedule is a series the lifecycle-driver alert group can page on — a
                // parked leg holds BY DESIGN, but never invisibly.
                LifecycleDriverMetrics.RecordScheduleHeld(CommandKindPayInstallment);
                continue;
            }

            var collectionAccountRef = DetailSerializer.Deserialize(loan.Detail).DisbursementAccountRef;

            // The ONE shared dispatch mapping (ADR-PC-036 §Decision 7): the same milestone→command
            // mapping the simulation forecast consumes — the occurrence key is the stable installment
            // NUMBER (the number-pin the whole double-collection safety rests on, §Decision 3 / LCD-1),
            // paid_at carries the due date as the business valid_time, and the decision presents the
            // SCOPED lifecycle money-mover principal (§Decision 1) the shared SCA gate admits. The
            // dispatch fitness test compares this against the forecast milestone for the same occurrence.
            decisions.Add(PersonalLoanLifecycleDispatch.PayInstallmentDecision(
                loan.StreamId, installmentNumber, dueDate, collectionAccountRef));
        }

        return decisions;
    }
}
