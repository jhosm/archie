namespace Babelstone.Families.CurrentAccount.Application;

// The current_account SETTLEMENT-facing surface's commands + HTTP contract (ADR-PC-043): the /credit and
// /capture endpoints the substrate SettlementProcess saga drives against the engine-owned CA. snake_case on
// the wire (the host's JSON options); money as integer cents — never a nested object or a float (ADR-PC-010).
// Every field is a structural value or a stable code / opaque reference — NO PII (ADR-PC-004): the
// account is the opaque path id, the intent reference is a process-derived token, never a name / NIF / IBAN.
//
// The exactly-once key on this surface is INVERTED from every other CA endpoint (ADR-PC-043,
// the scoped ADR-PC-029 carve-out): the append command_id is derived from the BODY's economic-intent
// reference (via SettlementReferences.DeriveFromIntent), NOT the HTTP Idempotency-Key — so a saga reissue
// with a fresh dispatch message_id but a byte-identical body collapses at command_dedup to ONE append.

/// <summary>The bounded credit-ADMISSION rejection taxonomy (ADR-PC-043): the
/// reasons a credit-receive is refused by construction, each a stable machine code the caller / reconciler
/// honours so an undeliverable credit is ATTRIBUTED, never silently dropped (ADR-PC-043
/// credit). An Active / Dormant account is ADMITTED (never a rejection code); only the genuinely-unreceivable
/// terminals refuse.</summary>
public static class CreditRejectedReason
{
    /// <summary>The account is CLOSED (a business terminal) — a credit cannot land; the source holds the
    /// funds in payout-pending and the lifecycle driver re-attempts against a live destination (ADR-PC-043 —
    /// hold at source).</summary>
    public const string AccountClosed = "ACCOUNT_CLOSED";

    /// <summary>The account is ERASED (GDPR Article 17 terminal) — a credit cannot land (ADR-PC-043
    /// credit-admission gate; no resurrection edge exists).</summary>
    public const string AccountErased = "ACCOUNT_ERASED";

    /// <summary>No account is open on this stream (Pending never opened, or Failed open) — there is nothing
    /// to credit, so the credit is refused rather than silently opening an account.</summary>
    public const string AccountNotOpen = "ACCOUNT_NOT_OPEN";
}

/// <summary>The intent behind one settlement CREDIT-receive attempt (ADR-PC-043): land
/// <see cref="AmountCents"/> as a Credit into account <see cref="AccountId"/>, if the account can receive it.
/// STRUCTURAL only — no PII (ADR-PC-004). The pure <see cref="CurrentAccountCreditAdmissionDecider"/>
/// turns it (plus the folded lifecycle) into an <see cref="Babelstone.Families.CurrentAccount.AccountCredited"/>
/// (Active) or a reactivate-then-credit batch (Dormant), or rejects a Closed/Erased account.</summary>
/// <param name="AccountId">The account stream being credited — the opaque <c>account_ref</c>, never PII.</param>
/// <param name="AmountCents">The credit to land, integer cents (ADR-PC-010) — the source <c>Movement.Amount</c>
/// (the in-band WRONG-AMOUNT guard, ADR-PC-043); the decider rejects a non-positive amount.</param>
/// <param name="ValueDate">The credit's economic effective value-date (ADR-PC-023).</param>
/// <param name="IntentReference">The ADR-PC-043 slot-4 economic-intent reference — the exactly-once axis the
/// append <see cref="CommandId"/> is derived from (NOT the HTTP Idempotency-Key). A structural token, never PII.</param>
/// <param name="Actor">The acting principal recorded on the append (a machine/saga settlement principal) — a role, never PII.</param>
/// <param name="CommandId">The append idempotency key (ADR-PC-029 slot 4), DERIVED from
/// <paramref name="IntentReference"/> — a byte-identical reissue collapses to ONE append at command_dedup.</param>
public sealed record ReceiveCreditCommand(
    Guid AccountId,
    long AmountCents,
    DateOnly ValueDate,
    string IntentReference,
    string Actor,
    Guid CommandId);

/// <summary>The intent behind one settlement CAPTURE attempt (ADR-PC-043): turn an authorize
/// reservation into a real debit — release the placed hold (<c>operations.HoldCaptured</c>) and land a Debit
/// <see cref="Babelstone.Families.CurrentAccount.AccountDebited"/> Movement, in ONE atomic append. STRUCTURAL
/// only — no PII (ADR-PC-004).</summary>
/// <param name="AccountId">The account stream being debited — the opaque <c>account_ref</c>, never PII.</param>
/// <param name="TargetHoldId">The reservation this capture settles — MUST equal the authorize's hold id for
/// one intent (ADR-PC-043; pinned by CurrentAccountCaptureTests); the spine captures WHERE the hold state is ACTIVE.</param>
/// <param name="AmountCents">The captured (settled) amount, integer cents — the source <c>Movement.Amount</c>
/// (the in-band WRONG-AMOUNT guard); may be less than the placed amount (a partial capture releases the
/// remainder, ADR-PC-037).</param>
/// <param name="ValueDate">The capture's economic effective value-date (ADR-PC-023).</param>
/// <param name="IntentReference">The ADR-PC-043 slot-4 economic-intent reference the append
/// <see cref="CommandId"/> is derived from (NOT the HTTP Idempotency-Key). A structural token, never PII.</param>
/// <param name="Actor">The acting principal recorded on the append (a machine/saga settlement principal) — a role, never PII.</param>
/// <param name="CommandId">The append idempotency key (ADR-PC-029 slot 4), DERIVED from
/// <paramref name="IntentReference"/> — a byte-identical reissue collapses to ONE append at command_dedup.</param>
public sealed record CaptureAccountCommand(
    Guid AccountId,
    string TargetHoldId,
    long AmountCents,
    DateOnly ValueDate,
    string IntentReference,
    string Actor,
    Guid CommandId);

/// <summary>Land a settlement CREDIT on the account (POST /v1/accounts/{id}/credit). The account is the path
/// id; the body carries the amount, its value-date, and the ADR-PC-043 economic-intent reference the append
/// command_id derives from — NOT the HTTP Idempotency-Key (the scoped ADR-PC-029 carve-out). The intent
/// reference is MANDATORY (a settlement credit has no fall-back key). STRUCTURAL only, no PII (ADR-PC-004).</summary>
/// <param name="AmountCents">The credit amount to land, integer cents (ADR-PC-010).</param>
/// <param name="ValueDate">The credit's economic effective value-date.</param>
/// <param name="IntentReference">The ADR-PC-043 slot-4 economic-intent reference — the exactly-once key.</param>
/// <param name="Actor">The acting principal recorded on the append (defaults to the settlement principal); a role, never PII.</param>
public sealed record ReceiveCreditRequest(
    long AmountCents,
    DateOnly ValueDate,
    string IntentReference,
    string? Actor = null);

/// <summary>Land a settlement CAPTURE on the account (POST /v1/accounts/{id}/capture). The account is the
/// path id; the body carries the target hold id (which MUST match the authorize's hold), the captured amount,
/// its value-date, and the ADR-PC-043 economic-intent reference the append command_id derives from — NOT the
/// HTTP Idempotency-Key. The intent reference is MANDATORY. STRUCTURAL only, no PII (ADR-PC-004).</summary>
/// <param name="TargetHoldId">The reservation to capture — the authorize's hold id (ADR-PC-043; pinned by CurrentAccountCaptureTests).</param>
/// <param name="AmountCents">The captured amount, integer cents (ADR-PC-010).</param>
/// <param name="ValueDate">The capture's economic effective value-date.</param>
/// <param name="IntentReference">The ADR-PC-043 slot-4 economic-intent reference — the exactly-once key.</param>
/// <param name="Actor">The acting principal recorded on the append (defaults to the settlement principal); a role, never PII.</param>
public sealed record CaptureAccountRequest(
    string TargetHoldId,
    long AmountCents,
    DateOnly ValueDate,
    string IntentReference,
    string? Actor = null);

/// <summary>A settlement-apply outcome (credit / capture): the account id, its folded lifecycle status, and
/// the commit sequence the append reached (the ADR-IC-005 read-your-writes token). Carries no PII — structural
/// facts only. For a capture, <see cref="Reconciliation"/> surfaces a non-normal hold release (a partial /
/// over-capture, ADR-PC-037), or null when the capture landed cleanly.</summary>
public sealed record SettlementApplyResponse(
    Guid AccountId,
    string Status,
    long CommitSequence,
    string? Reconciliation = null);
