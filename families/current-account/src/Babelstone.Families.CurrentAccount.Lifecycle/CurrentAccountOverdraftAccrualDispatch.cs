using Babelstone.Lifecycle;

namespace Babelstone.Families.CurrentAccount.Lifecycle;

/// <summary>
/// The current_account family's ONE overdraft-interest accrual command dispatch mapping (ADR-PC-037 §D5;
/// ADR-PC-036). In plain terms: "account A is drawn below zero as-of value-date D" must mean EXACTLY ONE wire
/// command — same command kind, same per-day occurrence key, same endpoint path, same body shape. This static,
/// pure mapping is the single source the production <see cref="OverdraftAccrualRule"/> derives its
/// <see cref="LifecycleCommandDecision"/> from.
/// </summary>
/// <remarks>
/// Two differences from the hold-expiry dispatch. (1) The occurrence key is the accrual DAY, not a placement
/// sequence: overdraft accrual is a RECURRING per-day charge (like the loan's per-installment step, not a
/// one-shot), so its occurrence is keyed on the accrual date's day ordinal (<see cref="DateOnly.DayNumber"/>) —
/// a stable long, so a re-tick or a backfilled retry of a given day re-derives the SAME id and the engine's
/// command_dedup swallows the repeat (LCD-1, ADR-PC-036). One accrual lands per account per day. (2) The route
/// posts a fee Movement, but it is an INTERNAL ledger charge (no external counterparty to settle), so — like
/// hold expiry, unlike the loan installment money-mover — it carries NO scoped SCA service principal. Pure: no
/// clock, no I/O — the same inputs always map to the same command (ADR-PC-010).
/// </remarks>
public static class CurrentAccountOverdraftAccrualDispatch
{
    /// <summary>The STABLE command-kind the accrual idempotency key is derived under. MUST match the kind the
    /// engine <c>/v1/accounts/{id}/overdraft/accrue</c> endpoint dedupes under so the driver-derived id and the
    /// engine-derived id are identical (LCD-1, ADR-PC-036).</summary>
    public const string CommandKindAccrueOverdraftInterest = "accrue_overdraft_interest";

    /// <summary>
    /// The ONE production command for "account <paramref name="accountId"/> is drawn below zero as-of
    /// <paramref name="accrualDate"/>, accrue that day's overdraft interest" — the
    /// <see cref="LifecycleCommandDecision"/> the driver's pass derives its number-pinned id from, dedupes, and
    /// POSTs (ADR-PC-036; ADR-PC-037 §D5).
    /// </summary>
    /// <param name="accountId">The account aggregate/stream the <c>OverdraftInterestAccrued</c> is appended to.</param>
    /// <param name="accrualDate">The day being accrued for — its <see cref="DateOnly.DayNumber"/> is the STABLE
    /// per-day occurrence key (one accrual per account per day), and it rides the body as the business
    /// valid_time the engine stamps (ADR-PC-002 / ADR-PC-023, a projection-derived input, never a clock read).</param>
    public static LifecycleCommandDecision AccrueDecision(Guid accountId, DateOnly accrualDate) =>
        new(
            InstanceId: accountId,
            CommandKind: CommandKindAccrueOverdraftInterest,
            // The accrual DAY's ordinal is the stable per-occurrence long (never a caller input beyond the
            // as-of date the driver owns): re-accruing day D re-derives the same id, so day D accrues once.
            OccurrenceKey: accrualDate.DayNumber,
            RequestPath: $"/v1/accounts/{accountId:D}/overdraft/accrue",
            // accrual_date carries the day's economic value-date as the business valid_time (ADR-PC-023 —
            // projection-derived, never a clock read). No PII rides the body (ADR-PC-004).
            Body: new Dictionary<string, object?> { ["accrual_date"] = accrualDate },
            DueAt: accrualDate,
            // NOT a rails money-mover: the fee is an internal ledger charge with no external settlement leg
            // (ADR-PC-037 §D5), so the route needs no scoped SCA service principal (contrast the loan installment).
            ServicePrincipalScope: null);
}
