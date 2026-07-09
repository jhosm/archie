namespace Babelstone.Families.CurrentAccount.Application;

// The current_account overdraft-interest ACCRUAL command shell's command + HTTP contract (ADR-PC-037 §D5):
// the target surface the ADR-PC-036 lifecycle-command driver POSTs to when it finds an account drawn below
// zero as-of a date. snake_case on the wire, the date as ISO-8601, NO PII (ADR-PC-004): the opaque account
// id (from the path) and the accrual's economic value-date only — the drawn balance and the rate are read
// command-side, never carried on the wire. The mandatory Idempotency-Key command id (the driver's canonical
// number-pinned dispatch id) rides the header, never the body (ADR-PC-029). Kept separate from the AUTHORIZE
// surface (AuthorizeContracts.cs) and the hold-expiry release (HoldExpiryContracts.cs): an accrual is a
// command-side money math + posting, neither.

/// <summary>
/// Accrue a day's overdraft interest (POST /v1/accounts/{id}/overdraft/accrue). In plain English: the
/// ADR-PC-036 driver spotted this account sitting below zero, so charge one day of overdraft interest — the
/// engine reads the current drawn balance, resolves the overdraft TAN from the rate sheet, and posts the fee
/// as a Debit Movement. This body carries only the accrual's economic <c>accrual_date</c> (the driver read it
/// from the overdraft projection horizon). Idempotent: a replayed accrual under the same
/// <c>Idempotency-Key</c> (ADR-PC-029) returns the original outcome with no second append — one accrual per
/// account per day.
/// </summary>
/// <param name="AccrualDate">The accrual's economic value-date — the business valid_time the engine stamps on
/// the OverdraftInterestAccrued, so a late or backfilled accrual records the correct economic date (ADR-PC-002
/// / ADR-PC-023). The driver supplies the day it is accruing for, never a clock read in a fold.</param>
/// <param name="Actor">The acting principal recorded on the append (defaults to the accrual driver principal),
/// a structural role, never PII.</param>
public sealed record OverdraftAccrualRequest(
    DateOnly AccrualDate,
    string? Actor = null);

/// <summary>The intent to accrue one day of overdraft interest on account <see cref="AccountId"/> (ADR-PC-037
/// §D5): read the drawn balance, resolve the overdraft TAN, and — if the account is a drawn current account
/// with an overdraft rate — append an <c>OverdraftInterestAccrued</c> fact carrying the fee as a Debit
/// Movement. STRUCTURAL only, no PII (ADR-PC-004). The fee is computed command-side (ADR-PC-037 §P3), never
/// carried on this command.</summary>
/// <param name="AccountId">The account stream the accrual is appended to and whose balance is read.</param>
/// <param name="AccrualDate">The accrual's economic value-date, carried onto the OverdraftInterestAccrued.</param>
/// <param name="Actor">The acting principal recorded on the append (the non-interactive driver principal), a
/// role, never PII.</param>
/// <param name="CommandId">The Idempotency-Key (ADR-PC-029 slot 4): a replay returns the ORIGINAL outcome with
/// no second append. MANDATORY on this command — the driver derives it as a number-pinned dispatch id so one
/// accrual lands per account per day (LCD-1, ADR-PC-036).</param>
public sealed record OverdraftAccrualCommand(
    Guid AccountId,
    DateOnly AccrualDate,
    string Actor,
    Guid CommandId);
