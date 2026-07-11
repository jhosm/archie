namespace Babelstone.Families.CurrentAccount.Application;

// The current_account HTTP contract (ADR-PC-021 boundary). snake_case on the wire (the host's JSON
// options); money as integer cents — never a nested object or a float (ADR-PC-010). Mirrors the
// personal-loan LoansContracts shape. Every field is a structural value or a stable code / opaque
// account reference — NO PII rides these (ADR-PC-004): the account id is an opaque instance identifier
// the engine resolves internally, never a name / NIF / IBAN.

/// <summary>
/// Open a new demand account (POST /v1/accounts). Account opening (KYC / onboarding) stays UPSTREAM
/// (ADR-PC-024 / ADR-PC-030); the engine records the opened account, it does not run the checks that
/// approve it. The account's balances are NOT set here — both the accounting and available balances are
/// spine-owned folds over the movement ledger + hold set (ADR-PC-033), zero at opening.
/// </summary>
/// <param name="AccountId">The account stream / aggregate id (caller-supplied) — the opaque instance
/// identifier that is also this account's <c>account_ref</c>, never PII (ADR-PC-004).</param>
/// <param name="ProductCode">The catalogue product code the account is opened under (e.g.
/// <c>ca_pt_standard</c>) — the structural product identifier, not PII.</param>
/// <param name="Currency">The account's ISO-4217 currency (e.g. <c>EUR</c>) — a structural token.</param>
/// <param name="OpenedAt">The instant the account opens and the event's valid time; host-stamped from the
/// wall clock when omitted so the decider stays pure (ADR-PC-010). The value-date is derived from it.</param>
/// <param name="Actor">The acting principal recorded on the append (defaults to the operations account-officer principal).</param>
public sealed record OpenAccountRequest(
    Guid AccountId,
    string ProductCode,
    string Currency,
    DateTimeOffset? OpenedAt = null,
    string? Actor = null);

/// <summary>Mark a live account dormant after an inactivity horizon (POST /v1/accounts/{id}/dormancy).
/// Dormant is a NON-terminal, reversible state — the account reactivates on use. The dormancy CRITERIA
/// (the inactivity horizon) are pack / product policy, not decided here (ADR-PC-037). Carries a mandatory
/// <c>Idempotency-Key</c> (ADR-PC-029).</summary>
/// <param name="Reason">A stable, non-PII dormancy reason code (e.g. <c>INACTIVITY_HORIZON</c>).</param>
/// <param name="MarkedAt">The instant dormancy takes effect; host-stamped when omitted.</param>
/// <param name="Actor">The acting principal (defaults to the operations account-officer principal).</param>
public sealed record MarkAccountDormantRequest(
    string Reason,
    DateTimeOffset? MarkedAt = null,
    string? Actor = null);

/// <summary>Reactivate a dormant account (POST /v1/accounts/{id}/reactivate) — the reverse leg of the
/// reversible Dormant ⇄ Active pair. Carries a mandatory <c>Idempotency-Key</c> (ADR-PC-029).</summary>
/// <param name="ReactivatedAt">The instant the account reactivates; host-stamped when omitted.</param>
/// <param name="Actor">The acting principal (defaults to the operations account-officer principal).</param>
public sealed record ReactivateAccountRequest(
    DateTimeOffset? ReactivatedAt = null,
    string? Actor = null);

/// <summary>Close a live account (POST /v1/accounts/{id}/close, → a business terminal). A closed account
/// still holds the subject's PII until erased, so GDPR erasure remains legal from Closed. Carries a
/// mandatory <c>Idempotency-Key</c> (ADR-PC-029).</summary>
/// <param name="ClosureReason">A stable, non-PII closure reason code (e.g. <c>CUSTOMER_REQUEST</c>).</param>
/// <param name="ClosedAt">The instant the account closes; host-stamped when omitted.</param>
/// <param name="Actor">The acting principal (defaults to the operations account-officer principal).</param>
public sealed record CloseAccountRequest(
    string ClosureReason,
    DateTimeOffset? ClosedAt = null,
    string? Actor = null);

/// <summary>GDPR Article 17 right-to-be-forgotten on an account (POST /v1/accounts/{id}/erase-personal-data):
/// record the erasure fact (the host has ALREADY crypto-shredded the subject's key — ADR-PC-004). Carries
/// a mandatory <c>Idempotency-Key</c> (ADR-PC-029) — key destruction is irreversible.</summary>
/// <param name="SubjectPseudonym">A salted one-way hash of the data-subject id — an OPAQUE reference,
/// NEVER the raw subject id (ADR-PC-004).</param>
/// <param name="ErasureReason">A stable machine code (e.g. <c>GDPR_ARTICLE_17</c>) — never PII.</param>
/// <param name="ErasedAt">The instant erasure took effect; host-stamped when omitted.</param>
/// <param name="Actor">The acting principal (defaults to the data-protection-officer principal).</param>
public sealed record ErasePersonalDataRequest(
    string SubjectPseudonym,
    string ErasureReason,
    DateTimeOffset? ErasedAt = null,
    string? Actor = null);

/// <summary>An account command outcome: the account id, its folded lifecycle status, and the commit
/// sequence the append reached (the ADR-IC-005 read-your-writes token). Carries no PII — structural
/// facts only.</summary>
public sealed record AccountCommandResponse(Guid AccountId, string Status, long CommitSequence);

/// <summary>An active hold on the account, as surfaced on the read view. A read shape over the
/// spine-owned active-hold fold (ADR-PC-033), never a stored source of truth; carries no PII (the hold id
/// and any court reference are structural). All money is integer cents (ADR-PC-010).</summary>
/// <param name="HoldId">The dedup / correlation key every lifecycle event of this hold carries.</param>
/// <param name="AmountCents">The earmarked amount in integer cents.</param>
/// <param name="ValueDate">An authorization hold's economic effective date (its expiry-horizon axis);
/// null for a legal hold.</param>
/// <param name="State">Where in its lifecycle this hold is (<c>Active</c> on the read view by query).</param>
/// <param name="Kind">Authorization or legal (ADR-PC-041) — the observable "why".</param>
/// <param name="LegalReference">A legal hold's court / case reference (ADR-PC-041); null for an
/// authorization hold. Structural, never PII.</param>
/// <param name="ExpiresAt">A legal hold's advisory expiry horizon; null = open-ended or an authorization hold.</param>
public sealed record AccountHoldView(
    string HoldId,
    long AmountCents,
    DateOnly? ValueDate,
    string State,
    string Kind,
    string? LegalReference,
    DateOnly? ExpiresAt);

/// <summary>
/// The account read view (GET /v1/accounts/{id}). The family record supplies only the structural /
/// lifecycle half (<see cref="AccountId"/> … <see cref="Status"/>); the two balances and the active-hold
/// set are read from the SPINE-owned folds (<c>AccountBalanceReader</c>), keyed by the account's opaque
/// <c>account_ref</c> — both computed, never stored (ACCOUNT_BALANCE_IS_A_FOLD, ADR-PC-033). All money is
/// integer cents (ADR-PC-010); no PII.
/// </summary>
public sealed record AccountResponse(
    Guid AccountId,
    string ProductCode,
    string Currency,
    DateOnly OpenedOn,
    string Status,
    long AccountingBalanceCents,
    long AvailableBalanceCents,
    IReadOnlyList<AccountHoldView> ActiveHolds);

/// <summary>One recorded movement line on the account statement, as surfaced on the read view. A read
/// shape over the spine-owned movement-ledger fold (ADR-PC-032), never a stored source of truth — the same
/// fold the accounting balance sums, exposed as its lines. STRUCTURAL columns only: no PII (ADR-PC-004 §P2)
/// — the movement carries no free-text detail / description / counterparty, only the closed-enum member
/// NAMES and integer cents. All money is integer cents (ADR-PC-010).</summary>
/// <param name="Direction"><c>Credit</c> or <c>Debit</c> relative to the account (the closed
/// <c>SettlementDirection</c> member name) — the sign the balance fold applies.</param>
/// <param name="AmountCents">The amount moved, integer cents.</param>
/// <param name="ValueDate">The economic date the value moved (the movement's value date).</param>
/// <param name="Operation">Which money move this records — the closed <c>MovementOperation</c> member
/// name (e.g. <c>Disburse</c>, <c>CollectInstallment</c>). A stable structural code, never PII.</param>
/// <param name="Origin"><c>Originated</c> or <c>Observed</c> — the closed <c>MovementOrigin</c> member
/// name.</param>
public sealed record MovementView(
    string Direction,
    long AmountCents,
    DateOnly ValueDate,
    string Operation,
    string Origin);

/// <summary>
/// The account movement-statement read view (GET /v1/accounts/{id}/movements). The account id and the
/// ordered movement lines are read from the SPINE-owned movement ledger (<c>AccountBalanceReader</c>),
/// keyed by the account's opaque <c>account_ref</c> (ADR-PC-032) — the same fold the accounting balance
/// sums, here exposed as its individual lines in stable (stream, sequence, index) order. Read-only, never a
/// stored source of truth. All money is integer cents (ADR-PC-010); no PII (ADR-PC-004 §P2) — the account
/// id is opaque and each line carries only structural closed-enum names, no free-text detail.
/// </summary>
public sealed record MovementsResponse(
    Guid AccountId,
    IReadOnlyList<MovementView> Movements);
