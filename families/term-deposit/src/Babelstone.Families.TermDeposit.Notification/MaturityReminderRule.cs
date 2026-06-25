using Babelstone.Notification;

namespace Babelstone.Families.TermDeposit.Notification;

/// <summary>
/// The term-deposit family's maturity-reminder schedule rule (ADR-IC-019 §D1 + Amendment 2026-06-24 /
/// ADR-PC-023 §6 / ADR-PC-025). In plain terms: the engine knows every deposit's maturity date but never
/// says "that date is now close" (it has no clock — ADR-PC-023); this rule reads the maturity calendar
/// as-of today and decides which deposits are entering the final pre-maturity opt-out window (02 §2.4.4) and
/// so are due a renewal reminder. It is the family-owned half of the notification subsystem — the generic
/// core owns the loop, the composite-id and the dedupe (<see cref="NotificationSchedulePass"/>); this rule
/// owns only the two term-deposit-shaped decisions: <em>which deposits are in the window</em> and <em>which
/// template the reminder carries</em>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this lives in the family, not the core (ADR-IC-019 §D1).</b> The opt-out-window width, the
/// <c>pt.notice.maturity</c> template ref, and the window-boundary decision are term-deposit domain
/// knowledge. Embedding them in the generic core was ADR-IC-019's explicitly-Rejected candidate C; this rule
/// is the relocation that resolves PR #317's §D1 violation (bd babelstone-9u70). It plugs into the core
/// through <see cref="INotificationScheduleRule"/> and is composed at the host edge via
/// <see cref="TermDepositNotificationModule"/>.
/// </para>
/// <para>
/// <b>The window value is pack-sourced, never a core literal (ADR-PC-007).</b> The PT pack carries the
/// canonical <c>AutoRenewalOptoutWindowDays</c> (the engine reads it fail-loud at the saga-start gate); this
/// downstream rule receives it from the host through configuration (<see cref="TermDepositNotificationModule"/>),
/// falling back to the family-side documented default. A deposit is "in the window" exactly when the engine's
/// saga-start gate would call it in-window: the window OPENS at <c>maturity_date − N days</c>
/// (TermDepositConstitutionService §3a), so a deposit is in-window when <c>maturity_date − N ≤ asOf</c>, i.e.
/// <c>maturity_date ≤ asOf + N</c>. As the engine's range-scan resource is half-open <c>[from, to)</c>,
/// catching every maturity up to AND INCLUDING <c>asOf + N</c> means scanning <c>[asOf, asOf + N + 1)</c>.
/// </para>
/// </remarks>
public sealed class MaturityReminderRule(DepositReadClient depositReadClient, int optOutWindowDays)
    : INotificationScheduleRule
{
    /// <summary>The pack-namespaced template for a maturity reminder (ADR-PC-025 slot 1 example
    /// <c>pt.notice.maturity</c>). One of the three parts of the composite notification key.</summary>
    public const string MaturityTemplateRef = "pt.notice.maturity";

    private readonly DepositReadClient _depositReadClient =
        depositReadClient ?? throw new ArgumentNullException(nameof(depositReadClient));

    private readonly int _optOutWindowDays = optOutWindowDays > 0
        ? optOutWindowDays
        : throw new ArgumentOutOfRangeException(
            nameof(optOutWindowDays), optOutWindowDays, "The opt-out window width must be positive.");

    /// <inheritdoc />
    public string FamilyName => TermDepositNotificationModule.Family;

    /// <summary>
    /// Read the maturity calendar for the <c>[asOf, asOf + N + 1)</c> window and, for each Active deposit
    /// entering the window, produce a <see cref="ReminderDecision"/> for the <c>pt.notice.maturity</c>
    /// template. The composite id and the dedupe are applied by the core's <see cref="NotificationSchedulePass"/>,
    /// so returning the same deposit on a later pass does not re-notify (ADR-PC-025 slot 4).
    /// </summary>
    public async Task<IReadOnlyList<ReminderDecision>> EvaluateAsync(DateOnly asOf, CancellationToken ct = default)
    {
        // The half-open window the engine's range-scan resource expects, matching the engine's own opt-out
        // gate: a deposit maturing exactly asOf + N is the FIRST day its opt-out right exists and must be
        // caught, so the scan is [asOf, asOf + N + 1). A deposit maturing TODAY is in-window too.
        var windowEnd = asOf.AddDays(_optOutWindowDays + 1);

        var maturing = await _depositReadClient.ListMaturitiesAsync(asOf, windowEnd, ct);

        var decisions = new List<ReminderDecision>();
        foreach (var deposit in maturing)
        {
            // Defensive: the range scan is bounded server-side, but a non-Active deposit (already Matured /
            // Renewed / Erased) is never a renewal-reminder target — its opt-out window is moot.
            if (!IsActive(deposit.Lifecycle))
            {
                continue;
            }

            decisions.Add(new ReminderDecision(
                InstanceId: deposit.DepositId,
                TemplateRef: MaturityTemplateRef,
                OccurrenceKey: deposit.MaturityDate,
                DueAt: asOf,
                Amounts: new Dictionary<string, long>
                {
                    ["total_payout_cents"] = deposit.TotalPayoutCents,
                    ["net_interest_cents"] = deposit.NetInterestCents,
                }));
        }

        return decisions;
    }

    private static bool IsActive(string lifecycle) =>
        string.Equals(lifecycle, "Active", StringComparison.OrdinalIgnoreCase);
}
