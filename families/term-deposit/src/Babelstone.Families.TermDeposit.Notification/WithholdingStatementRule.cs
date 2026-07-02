using Babelstone.Notification;

namespace Babelstone.Families.TermDeposit.Notification;

/// <summary>
/// The term-deposit family's annual IRS-withholding statement rule (ADR-IC-019 §D1 + Amendment 2026-06-24 /
/// ADR-PC-023 §6 / ADR-PC-025). In plain terms: once a year the bank must send each depositor a statement of
/// the tax it withheld on their interest. The engine has no clock and never emits this on a date arriving
/// (ADR-PC-023); this rule is the downstream scheduler's family-shaped half — it reads the deposits that have
/// had tax withheld (and their accrual/withholding rollups) and produces one SCHEDULED statement decision per
/// deposit for the prior tax year. It is the sibling of <see cref="MaturityReminderRule"/>: same family-owned
/// shape (which instances are due, which <c>template_ref</c> they carry), but it reads the withholding
/// population instead of the maturity calendar, and its cadence is annual rather than a daily window.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this lives in the family, not the core (ADR-IC-019 §D1).</b> The <c>pt.notice.withholding_statement</c>
/// template ref, the annual cadence and the "this deposit had withholding → a statement is due" decision are
/// term-deposit domain knowledge, kept OUT of the generic core exactly as the maturity rule's are. It plugs
/// into the core through <see cref="INotificationScheduleRule"/> and is composed at the host edge via
/// <see cref="TermDepositNotificationModule"/>. The composite-id derivation and the "re-runs don't re-notify"
/// dedupe stay CORE primitives (<see cref="NotificationSchedulePass"/> / ADR-PC-025 slot 4) — this rule never
/// reimplements idempotency.
/// </para>
/// <para>
/// <b>Idempotent annual cadence (ADR-PC-025 slot 4).</b> The occurrence the composite <c>notification_id</c>
/// is keyed on is the tax-year boundary (<c>31 Dec</c> of the prior calendar year), fixed per deposit per
/// year. So every pass in a given calendar year re-derives the SAME id for the same deposit and the dedupe
/// ledger absorbs it; the next calendar year keys a new occurrence and a fresh statement is raised. The
/// clock lives one layer up in the worker loop (ADR-PC-023 §6), so the rule is a deterministic function of
/// the as-of date and trivially testable.
/// </para>
/// <para>
/// <b>Per-tax-year figure.</b> The statement reports the withholding for the PRIOR
/// TAX YEAR, not a cumulative-to-date figure. The rule reads each deposit's DATED withholding ledger
/// (<c>GET /v1/deposits/{id}/withholding-ledger</c> — the <c>withholding_ledger</c> projection's per-flow
/// dates now exposed over the read surface, ADR-PC-027 / ADR-IC-019 §D3) and SUMS only the flows withheld in
/// that tax year, replacing the earlier cumulative <c>withholding_to_date</c> approximation. A deposit whose
/// withholding all fell in a different year contributes no statement for this one. No PII rides the decision
/// (ADR-PC-025 PII rule) — only the structural cents figures; the depositor's name/NIF is resolved by
/// reference at render time. The three <c>Amounts</c> keys are kept stable for the
/// <c>pt.notice.withholding_statement</c> template contract; their VALUES are now the tax-year slice.
/// </para>
/// <para>
/// <b>Pre-field withholding flows fail loud, not silent.</b> A
/// <c>WithholdingApplied</c> event stored BEFORE the <c>WithheldOn</c> field existed
/// folds to <c>default(DateOnly)</c> = <c>0001-01-01</c> on replay — deterministic, no clock, so §P5 holds.
/// Such a flow carries NO recoverable tax year, so the <c>entry.WithheldOn.Year == taxYear</c> slice below
/// would SILENTLY drop it from EVERY per-tax-year statement — a legacy depositor would never receive the
/// statutory withholding statement they are owed, and a deposit that ALSO has dated flows would emit a
/// statement that silently under-reports (the pre-field flow omitted from the sum). The rule therefore
/// SURFACES an un-dated flow (throws, naming the deposit) rather than under-reporting, so the stream is
/// backfilled first — the same fail-loud discipline the engine applies to an empty funding reference.
/// </para>
/// </remarks>
public sealed class WithholdingStatementRule(DepositReadClient depositReadClient) : INotificationScheduleRule
{
    /// <summary>The pack-namespaced template for an annual IRS-withholding statement (ADR-PC-025 slot 1).
    /// One of the three parts of the composite notification key.</summary>
    public const string WithholdingStatementTemplateRef = "pt.notice.withholding_statement";

    private readonly DepositReadClient _depositReadClient =
        depositReadClient ?? throw new ArgumentNullException(nameof(depositReadClient));

    /// <inheritdoc />
    public string FamilyName => TermDepositNotificationModule.Family;

    /// <summary>
    /// Read every deposit that has had tax withheld and, for each one that can still receive a statement AND
    /// actually withheld tax in the PRIOR tax year, produce a <see cref="ReminderDecision"/> for the
    /// <c>pt.notice.withholding_statement</c> template keyed on that tax year — carrying the per-tax-year
    /// slice of its DATED withholding ledger, not the cumulative-to-date figure. The
    /// composite id and the dedupe are applied by the core's <see cref="NotificationSchedulePass"/>, so
    /// returning the same deposit on a later pass in the same year does not re-notify (ADR-PC-025 slot 4).
    /// </summary>
    public async Task<IReadOnlyList<ReminderDecision>> EvaluateAsync(DateOnly asOf, CancellationToken ct = default)
    {
        // The statement covers the prior completed calendar (tax) year; the occurrence the composite id is
        // keyed on is that year's boundary, so the annual cadence is the dedupe ledger absorbing every repeat
        // within the year and admitting exactly one statement per deposit per tax year.
        var taxYear = asOf.Year - 1;
        var taxYearEnd = new DateOnly(taxYear, 12, 31);

        var deposits = await _depositReadClient.ListWithholdingStatementsAsync(ct);

        var decisions = new List<ReminderDecision>();
        foreach (var deposit in deposits)
        {
            // A crypto-shredded (Erased) deposit cannot be rendered — its render-time PII reference is gone —
            // so it is never a statement target, even though its financial facts (and withholding) remain.
            if (IsErased(deposit.Lifecycle))
            {
                continue;
            }

            // Slice the deposit's DATED withholding ledger to the target tax year:
            // sum ONLY the flows withheld in that calendar/tax year — the per-year figure, replacing the
            // cumulative withholding_to_date the v1 approximation reported. The ledger read is empty for a
            // deposit with no materialised withholding flow yet (404 → []), which yields no slice.
            var ledger = await _depositReadClient.GetWithholdingLedgerAsync(deposit.DepositId, ct);

            // An un-dated flow (WithheldOn == default, i.e. 0001-01-01) carries no recoverable tax year, so it
            // must be backfilled before it can be sliced — surface it rather than silently drop (see the
            // <para> on the class for the full fail-loud rationale).
            if (ledger.Any(entry => entry.WithheldOn == default))
            {
                throw new InvalidOperationException(
                    $"Deposit {deposit.DepositId} has a pre-field WithholdingApplied flow with no withheld-on " +
                    "date (default 0001-01-01) — it cannot be sliced to a tax year and must be backfilled before " +
                    "an annual IRS-withholding statement can be scheduled.");
            }

            var yearFlows = ledger.Where(entry => entry.WithheldOn.Year == taxYear).ToList();

            // No withholding in the target tax year → no statement is due for THAT year. A deposit whose
            // withholding all fell in another year does not get a statement keyed on this one (the cumulative
            // v1 figure would have wrongly raised one).
            if (yearFlows.Count == 0)
            {
                continue;
            }

            decisions.Add(new ReminderDecision(
                InstanceId: deposit.DepositId,
                TemplateRef: WithholdingStatementTemplateRef,
                OccurrenceKey: taxYearEnd,
                DueAt: asOf,
                // The three keys are kept stable for the pt.notice.withholding_statement template contract;
                // their VALUES are now the tax-year slice (the sum of that year's per-flow figures), never
                // the cumulative-to-date rollup.
                Amounts: new Dictionary<string, long>
                {
                    ["accrued_gross_interest_cents"] = yearFlows.Sum(entry => entry.GrossCents),
                    ["withholding_to_date_cents"] = yearFlows.Sum(entry => entry.TaxCents),
                    ["net_interest_cents"] = yearFlows.Sum(entry => entry.NetCents),
                }));
        }

        return decisions;
    }

    private static bool IsErased(string lifecycle) =>
        string.Equals(lifecycle, "Erased", StringComparison.OrdinalIgnoreCase);
}
