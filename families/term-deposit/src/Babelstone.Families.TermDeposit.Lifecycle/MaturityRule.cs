using Babelstone.Families.TermDeposit;
using Babelstone.Lifecycle;

namespace Babelstone.Families.TermDeposit.Lifecycle;

/// <summary>
/// The term-deposit family's lifecycle-command rule (ADR-PC-036 §Decision 2 + §Decision 6; bd
/// babelstone-6cpq.8) — the ONE-SHOT maturity case of the driver's per-family <see cref="ILifecycleCommandRule"/>
/// port. In plain terms: the engine knows every deposit's maturity date but owns no clock to act on it
/// (ADR-PC-023); this rule reads the deposit read model as-of today and says "these deposits have reached
/// maturity, fire <c>Mature</c> on each", and the generic driver derives the canonical id, dedupes, and POSTs.
/// It is the write-side twin of the notification estate's <c>MaturityReminderRule</c> — same family, same
/// maturity calendar read, but it fires the state-changing command rather than raising a reminder.
/// </summary>
/// <remarks>
/// <para>
/// <b>Fires on/after the maturity date, never before — the one ordering invariant this rule owns</b>
/// (ADR-PC-036 §Residual risks). The scan is the half-open window <c>[DateOnly.MinValue, asOf + 1)</c>, so a
/// deposit maturing TODAY is caught (the boundary is inclusive of today) and a deposit maturing tomorrow is
/// NOT — the driver cannot trip the renewal opt-out window by firing early. The unbounded lower bound gives
/// <b>correct backfill by construction</b> (ADR-PC-036 §S2): a deposit that matured while the driver was down
/// is still <see cref="DepositLifecycle.Active"/> with a past maturity date, so the next pass re-derives it
/// under the SAME number-pinned id and the engine stamps its own maturity date as the business
/// <c>valid_time</c> (the body carries <c>matured_at</c>), recording the correct business date for a late
/// firing (ADR-PC-002 bitemporality).
/// </para>
/// <para>
/// <b>Inherits the renewal opt-out / saga-start gates; encodes none of its own</b> (ADR-PC-036 §Decision 6,
/// bd babelstone-mtto.3, already built). Firing <c>Mature</c> emits <c>DepositMatured</c>, which auto-starts
/// the renewal saga only on a non-<c>NONE</c> policy, and the built saga-start gate rejects any renewal dated
/// before maturity — both upstream of this rule. So this rule fires maturity on/after the due date and adds NO
/// renewal-suppression check. <b>Maturity is the degenerate single-occurrence case</b> — the occurrence key is
/// the constant <c>1</c> (ADR-PC-036 §Decision 3), so a re-tick or backfill re-derives the SAME id and no
/// catch-up / settlement-health gate is needed (that gate is a recurring-installment concern, LCD-2).
/// </para>
/// <para>
/// The non-Active filter is the safety net that keeps an already-matured deposit (lifecycle <c>Matured</c> /
/// <c>Renewed</c> / terminal) out of the firing set, so the driver never re-POSTs a maturity the engine would
/// reject — the engine's <c>command_dedup</c> on the constant occurrence id is the authoritative backstop
/// regardless (ADR-PC-029 slot 4).
/// </para>
/// </remarks>
public sealed class MaturityRule(IDepositReadModelStore deposits) : ILifecycleCommandRule
{
    /// <summary>The STABLE command-kind the maturity idempotency key is derived under. MUST equal the engine
    /// maturity endpoint's own derivation kind (<c>DepositsEndpoints.MatureCommandKind = "mature"</c>) so the
    /// driver-derived id and the engine-derived id are identical (LCD-1, ADR-PC-036 §Decision 1+3) — named
    /// here, not referenced, to keep the driver free of a family-application compile dependency, the same
    /// lock-step-by-constant discipline <c>HttpLifecycleCommandSink</c> uses for its headers.</summary>
    public const string CommandKindMature = "mature";

    /// <summary>The scoped, non-interactive SCA service principal the deposit money-mover route authorises the
    /// driver by (ADR-PC-036 §Decision 1). Kept in lock-step with the engine-side
    /// <c>ScaServicePrincipal.LifecycleMoneyMoverScope</c>; named locally (not referenced) so the driver core
    /// takes no dependency on the term-deposit Application assembly.</summary>
    public const string DepositMoneyMoverScope = "lifecycle:deposit-money-mover";

    // Maturity is the degenerate ONE-SHOT occurrence (exactly one per deposit), so its stable occurrence key
    // is the constant 1 — the engine maturity endpoint derives its key under the same constant.
    private const long MaturityOccurrence = 1;

    private readonly IDepositReadModelStore _deposits =
        deposits ?? throw new ArgumentNullException(nameof(deposits));

    /// <inheritdoc />
    public string FamilyName => "term_deposit";

    /// <summary>
    /// Produce a <c>Mature</c> command for every Active deposit whose maturity date is on or before
    /// <paramref name="asOf"/>. The driver's pass derives each decision's number-pinned id and dedupes it, so
    /// returning the same still-due deposit on every pass fires it at most once (ADR-PC-036 §Decision 2/3).
    /// </summary>
    public async Task<IReadOnlyList<LifecycleCommandDecision>> EvaluateAsync(
        DateOnly asOf, CancellationToken ct = default)
    {
        // Half-open [MinValue, asOf + 1): every deposit maturing on/before today (today inclusive, tomorrow
        // excluded), with no lower bound so a maturity missed during an outage is still caught (backfill).
        var matured = await _deposits.ListByMaturityAsync(DateOnly.MinValue, asOf.AddDays(1), ct);

        var decisions = new List<LifecycleCommandDecision>();
        foreach (var deposit in matured)
        {
            // A non-Active deposit (already Matured / Renewed / terminal) is never a maturity target — its
            // maturity has happened. The range scan is by date alone, so filter the lifecycle here.
            if (!IsActive(deposit.Lifecycle))
            {
                continue;
            }

            decisions.Add(new LifecycleCommandDecision(
                InstanceId: deposit.StreamId,
                CommandKind: CommandKindMature,
                OccurrenceKey: MaturityOccurrence,
                RequestPath: $"/v1/deposits/{deposit.StreamId:D}/maturity",
                // matured_at carries the deposit's OWN maturity date as the business valid_time, so a late
                // firing still records the correct business date (ADR-PC-036 §Context; ADR-PC-002). The payout
                // account defaults engine-side; the body carries no PII (ADR-PC-004 §P2).
                Body: new Dictionary<string, object?> { ["matured_at"] = AtUtcMidnight(deposit.MaturityDate) },
                DueAt: deposit.MaturityDate,
                ServicePrincipalScope: DepositMoneyMoverScope));
        }

        return decisions;
    }

    private static bool IsActive(string lifecycle) =>
        string.Equals(lifecycle, nameof(DepositLifecycle.Active), StringComparison.OrdinalIgnoreCase);

    // The due date rides as a value (ADR-PC-036 §Context): a DateOnly maturity date as UTC midnight, the wire
    // shape the engine endpoint's DateTimeOffset? MaturedAt binds and stamps the event's valid_time from.
    private static DateTimeOffset AtUtcMidnight(DateOnly date) =>
        new(date.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
}
