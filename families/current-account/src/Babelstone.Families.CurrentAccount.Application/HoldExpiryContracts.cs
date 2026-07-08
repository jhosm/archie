namespace Babelstone.Families.CurrentAccount.Application;

// The current_account hold-EXPIRY command shell's command + HTTP contract (ADR-PC-037): the target surface
// the ADR-PC-036 lifecycle-command driver POSTs to. snake_case on the wire; the date as ISO-8601; NO PII
// (ADR-PC-004): opaque account/hold ids + the economic value-date only. The mandatory Idempotency-Key command
// id rides the header, never the body (ADR-PC-029). Kept separate from the AUTHORIZE surface
// (AuthorizeContracts.cs) and the account state-machine lifecycle (LifecycleCommands.cs): expiry is a
// hold-lifecycle release, neither.

/// <summary>
/// Expire an authorization hold (POST /v1/accounts/{id}/holds/{holdId}/expire). In plain English: an
/// approved-but-unsettled earmark has reached its value-date horizon and never settled, so release it — no
/// money moves. The account is the path id and the hold is the path <c>holdId</c>; this body carries only the
/// hold's economic <c>value_date</c> (the driver read it from the expiry projection). De-settled and posting-
/// free: a HoldExpired ends the earmark with NO Movement (ADR-PC-037). Carries a mandatory
/// <c>Idempotency-Key</c> (ADR-PC-029) — a replayed expiry returns the original outcome with no second append.
/// </summary>
/// <param name="ValueDate">The hold's economic value-date — the business valid_time the engine stamps on the
/// HoldExpired, so a late/backfilled expiry records the correct economic date (ADR-PC-002 / ADR-PC-023).</param>
/// <param name="Actor">The acting principal recorded on the append (defaults to the lifecycle hold-expiry
/// driver principal); a structural role, never PII.</param>
public sealed record ExpireHoldRequest(
    DateOnly ValueDate,
    string? Actor = null);

/// <summary>The intent to expire one authorization hold (ADR-PC-037): append a <c>HoldExpired</c>
/// release fact for hold <see cref="HoldId"/> on account <see cref="AccountId"/>. STRUCTURAL only — no PII
/// (ADR-PC-004). The pure spine fold treats the event as a no-op on the family position (holds are
/// spine-owned, ADR-PC-033); the <c>AccountHoldProjector</c> transitions the account_holds row out of the
/// ACTIVE set, or surfaces a reconciliation signal if it already left it (a late/duplicate release).</summary>
/// <param name="AccountId">The account stream the HoldExpired is appended to.</param>
/// <param name="HoldId">The ADR-PC-033 slot-4 lifecycle key of the hold being expired — from the path.</param>
/// <param name="ValueDate">The hold's economic value-date, carried onto the HoldExpired event.</param>
/// <param name="Actor">The acting principal recorded on the append (the non-interactive driver principal) — a
/// role, never PII.</param>
/// <param name="CommandId">The Idempotency-Key (ADR-PC-029 slot 4): a replay returns the ORIGINAL outcome with
/// no second append. MANDATORY on this command.</param>
public sealed record ExpireHoldCommand(
    Guid AccountId,
    string HoldId,
    DateOnly ValueDate,
    string Actor,
    Guid CommandId);
