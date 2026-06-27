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
/// <b>v1 figure (documented approximation).</b> The read-model rollup carries withholding TO DATE (cumulative
/// across the deposit's life), not a per-tax-year slice — a true per-year breakdown needs the dated entries of
/// the <c>withholding_ledger</c> projection, which is not yet exposed over the read surface. v1 reports the
/// cumulative figure as-of the run, labelled with the prior tax year; the per-year split is a documented
/// follow-up. No PII rides the decision (ADR-PC-025 PII rule) — only the structural cents figures; the
/// depositor's name/NIF is resolved by reference at render time.
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
    /// Read every deposit that has had tax withheld and, for each one that can still receive a statement,
    /// produce a <see cref="ReminderDecision"/> for the <c>pt.notice.withholding_statement</c> template keyed
    /// on the prior tax year. The composite id and the dedupe are applied by the core's
    /// <see cref="NotificationSchedulePass"/>, so returning the same deposit on a later pass in the same year
    /// does not re-notify (ADR-PC-025 slot 4).
    /// </summary>
    public async Task<IReadOnlyList<ReminderDecision>> EvaluateAsync(DateOnly asOf, CancellationToken ct = default)
    {
        // The statement covers the prior completed calendar (tax) year; the occurrence the composite id is
        // keyed on is that year's boundary, so the annual cadence is the dedupe ledger absorbing every repeat
        // within the year and admitting exactly one statement per deposit per tax year.
        var taxYearEnd = new DateOnly(asOf.Year - 1, 12, 31);

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

            decisions.Add(new ReminderDecision(
                InstanceId: deposit.DepositId,
                TemplateRef: WithholdingStatementTemplateRef,
                OccurrenceKey: taxYearEnd,
                DueAt: asOf,
                Amounts: new Dictionary<string, long>
                {
                    ["accrued_gross_interest_cents"] = deposit.AccruedGrossInterestCents,
                    ["withholding_to_date_cents"] = deposit.WithholdingToDateCents,
                    ["net_interest_cents"] = deposit.NetInterestCents,
                }));
        }

        return decisions;
    }

    private static bool IsErased(string lifecycle) =>
        string.Equals(lifecycle, "Erased", StringComparison.OrdinalIgnoreCase);
}
