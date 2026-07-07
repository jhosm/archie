namespace Babelstone.Families.CurrentAccount.Application;

// The current_account synchronous AUTHORIZE surface's command + HTTP contract (ADR-PC-037 §D6 /
// ADR-PC-034). snake_case on the wire (the host's JSON options); money as integer cents — never a
// nested object or a float (ADR-PC-010). Every field is a structural value or a stable code — NO PII
// (ADR-PC-004): the account is the opaque path id; the body carries only the attempted amount, its
// value-date, and the acting principal. The mandatory Idempotency-Key command id rides the header, not
// the body (ADR-PC-029).

/// <summary>The bounded declined taxonomy (ADR-PC-037 §D6): the only four reasons a synchronous
/// authorize is refused, each a stable machine code the caller must honour (the decision is gated,
/// ADR-PC-033 slot 5). Layered OVER the engine's generic <c>AuthorizationDeclineReason</c> (which is
/// narrower — it folds overdraft into insufficient-balance and knows no account lifecycle): the family
/// decider maps the spine reasons onto these product codes and adds the lifecycle gate.</summary>
public static class AccountDeclinedReason
{
    /// <summary>The available balance — net of active holds — does not cover the debit, and no arranged
    /// overdraft was configured to absorb the shortfall.</summary>
    public const string InsufficientAvailableBalance = "INSUFFICIENT_AVAILABLE_BALANCE";

    /// <summary>The debit would take the account beyond its arranged overdraft (*descoberto autorizado*)
    /// — an unarranged overdraft (*ultrapassagem*) is refused (ADR-PC-037 §D5).</summary>
    public const string OverdraftLimitExceeded = "OVERDRAFT_LIMIT_EXCEEDED";

    /// <summary>The debit breaches a pack transaction limit — the per-transaction ceiling (windowed
    /// daily/monthly velocity limits arrive with the pack-rule read).</summary>
    public const string LimitExceeded = "LIMIT_EXCEEDED";

    /// <summary>The account is not in a state that can authorize a debit — dormant, closed, failed, or
    /// under a compliance freeze (blocked). Every operating transition is legal only from Active
    /// (ADR-PC-037 §D2).</summary>
    public const string AccountNotActive = "ACCOUNT_NOT_ACTIVE";
}

/// <summary>The two verdicts a synchronous authorize can answer (ADR-PC-034): the debit was authorized
/// (an earmark placed) or declined (a refusal recorded). Stable wire tokens, never PII.</summary>
public static class AuthorizeOutcomes
{
    public const string Authorized = "AUTHORIZED";
    public const string Declined = "DECLINED";
}

/// <summary>The intent behind one synchronous authorize attempt (ADR-PC-037 §D6): may
/// <see cref="AmountCents"/> be debited from account <see cref="AccountId"/> right now, and if so,
/// earmark it. STRUCTURAL only — no PII (ADR-PC-004). The pure
/// <see cref="CurrentAccountAuthorizeDecider"/> turns it (plus the read balance / freeze / pack rules)
/// into an <c>operations.HoldPlaced</c> earmark or an <see cref="Babelstone.Families.CurrentAccount.AuthorizationDeclined"/>
/// refusal fact.</summary>
/// <param name="AccountId">The account stream being debited — the opaque <c>account_ref</c>, never PII.</param>
/// <param name="AmountCents">The debit to authorize, integer cents (ADR-PC-010); the shell rejects a non-positive amount.</param>
/// <param name="ValueDate">The debit's economic effective date — the hold's expiry-horizon axis (ADR-PC-023).</param>
/// <param name="Actor">The acting principal recorded on the append (a machine/rail authorize principal) — a role, never PII.</param>
/// <param name="CommandId">The caller's Idempotency-Key (ADR-PC-029 slot 4): a replay returns the ORIGINAL
/// verdict with no second append. MANDATORY on this money-mover path.</param>
public sealed record AuthorizeAccountCommand(
    Guid AccountId,
    long AmountCents,
    DateOnly ValueDate,
    string Actor,
    Guid CommandId);

/// <summary>Authorize a debit (POST /v1/accounts/{id}/authorize). The account is the path id; the body
/// carries the attempted amount and its value-date. The Idempotency-Key command id is a MANDATORY header
/// (ADR-PC-029), not a body field. De-settled: a <c>2xx</c> confirms the verdict was decided and appended,
/// NOT that cash moved — capture arrives later as <c>HoldCaptured</c> (ADR-PC-034 property 2).</summary>
/// <param name="AmountCents">The debit amount to authorize, integer cents (ADR-PC-010).</param>
/// <param name="ValueDate">The debit's economic effective date.</param>
/// <param name="Actor">The acting principal recorded on the append (defaults to the payment-authorize principal); a role, never PII.</param>
public sealed record AuthorizeRequest(
    long AmountCents,
    DateOnly ValueDate,
    string? Actor = null);

/// <summary>The synchronous authorize verdict (ADR-PC-037 §D6). <see cref="Outcome"/> is
/// <c>AUTHORIZED</c> (then <see cref="HoldId"/> carries the placed <c>operations.HoldPlaced</c>) or
/// <c>DECLINED</c> (then <see cref="DeclinedReason"/> carries a bounded <see cref="AccountDeclinedReason"/>
/// code and the refusal is an appended auditable fact, not a silent non-append). A DECLINED is a normal
/// business outcome returned on the <c>200</c>, not an HTTP error. Carries no PII — structural facts
/// only.</summary>
/// <param name="AccountId">The account's stream id.</param>
/// <param name="Outcome"><c>AUTHORIZED</c> or <c>DECLINED</c> (<see cref="AuthorizeOutcomes"/>).</param>
/// <param name="HoldId">The placed hold's correlation id; present only on <c>AUTHORIZED</c>, null on <c>DECLINED</c>.</param>
/// <param name="DeclinedReason">The bounded refusal code; present only on <c>DECLINED</c>, null on <c>AUTHORIZED</c>.</param>
/// <param name="CommitSequence">The per-stream commit sequence this verdict reached — the read-your-writes token (ADR-IC-005 §P3).</param>
public sealed record AuthorizeResponse(
    Guid AccountId,
    string Outcome,
    string? HoldId,
    string? DeclinedReason,
    long CommitSequence);
