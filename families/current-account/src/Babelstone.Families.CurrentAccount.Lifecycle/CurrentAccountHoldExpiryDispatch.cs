using Babelstone.Lifecycle;

namespace Babelstone.Families.CurrentAccount.Lifecycle;

/// <summary>
/// The current_account family's ONE hold-expiry command dispatch mapping (ADR-PC-037; ADR-PC-036
///). In plain terms: "authorization hold H on account A is due to expire on value-date V" must
/// mean EXACTLY ONE wire command — same command kind, same per-hold occurrence key, same endpoint path, same
/// body shape. This static, pure mapping is the single source the production <see cref="HoldExpiryRule"/>
/// derives its <see cref="LifecycleCommandDecision"/> from.
/// </summary>
/// <remarks>
/// Unlike the term-deposit maturity dispatch, a hold expiry has NO simulation-forecast counterpart (a
/// current account's holds are a spine-owned fold, not part of the family's simulated forward lifecycle), so
/// there is no forecast-vs-production dispatch fitness test here — this class is simply the one pure place the
/// command shape lives. Two further differences from maturity: (1) the occurrence key is NOT the one-shot
/// constant <c>1</c> but the placing event's per-stream sequence — a hold is one of MANY on its account, so
/// its occurrence must be keyed on which placement it releases (ADR-PC-036, a stable long, since
/// <c>hold_id</c> is a string); (2) expiry moves NO money (a <c>HoldExpired</c> is a release with no posting,
/// ADR-PC-037), so the route is NOT a money-mover and carries NO SCA service-principal scope. Pure: no
/// clock, no I/O — the same inputs always map to the same command (ADR-PC-010).
/// </remarks>
public static class CurrentAccountHoldExpiryDispatch
{
    /// <summary>The STABLE command-kind the hold-expiry idempotency key is derived under. MUST equal the kind
    /// the engine <c>/v1/accounts/{id}/holds/{holdId}/expire</c> endpoint dedupes under so the driver-derived
    /// id and the engine-derived id are identical (LCD-1, ADR-PC-036).</summary>
    public const string CommandKindExpireHold = "expire_hold";

    /// <summary>
    /// The ONE production command for "authorization hold <paramref name="holdId"/> on account
    /// <paramref name="accountId"/> is due to expire on <paramref name="valueDate"/>" — the
    /// <see cref="LifecycleCommandDecision"/> the driver's pass derives its number-pinned id from, dedupes,
    /// and POSTs (ADR-PC-036; ADR-PC-037).
    /// </summary>
    /// <param name="accountId">The account aggregate/stream the <c>HoldExpired</c> is appended to.</param>
    /// <param name="holdId">The ADR-PC-033 slot-4 lifecycle key of the hold being expired — rides the path.</param>
    /// <param name="placedSequence">The per-stream sequence of the placing event: the STABLE per-hold
    /// occurrence key (a string <paramref name="holdId"/> cannot be one, ADR-PC-036), so a re-tick
    /// re-derives the SAME id and the engine's command_dedup swallows the repeat.</param>
    /// <param name="valueDate">The hold's OWN value-date — rides the body as the business valid_time the
    /// engine stamps, so a late/backfilled expiry records the correct economic date (ADR-PC-002).</param>
    public static LifecycleCommandDecision ExpireDecision(
        Guid accountId, string holdId, long placedSequence, DateOnly valueDate) =>
        new(
            InstanceId: accountId,
            CommandKind: CommandKindExpireHold,
            OccurrenceKey: placedSequence,
            RequestPath: $"/v1/accounts/{accountId:D}/holds/{holdId}/expire",
            // value_date carries the hold's OWN economic date as the business valid_time (ADR-PC-023 —
            // projection-derived, never a clock read). No PII rides the body (ADR-PC-004).
            Body: new Dictionary<string, object?> { ["value_date"] = valueDate },
            DueAt: valueDate,
            // NOT a money-mover: a HoldExpired releases the earmark with no posting (ADR-PC-037), so
            // the route needs no scoped SCA service principal (contrast maturity, which pays out).
            ServicePrincipalScope: null);
}
