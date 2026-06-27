namespace Babelstone.Families.PersonalLoan.Application;

// The personal_loan HTTP contract (ADR-PC-021 §D5 boundary). snake_case on the wire (the host's JSON
// options); money as integer cents — never a nested object or a float (ADR-PC-010 §P1). Mirrors the
// term-deposit DepositsContracts shape. Every field is a structural value or a stable code / opaque
// account reference — NO PII rides these (ADR-PC-004 §P2): account fields are OPAQUE tokens the engine
// resolves internally, never an IBAN.

/// <summary>
/// Disburse a personal loan (POST /v1/loans): the already-approved, already-priced loan's principal, term,
/// and pricing inputs fixed at constitution (ADR-PC-030 / ADR-PC-024 — origination is upstream). The engine
/// resolves the rate sheet, computes the French amortization schedule, and appends <c>LoanDisbursed</c> with
/// its money leg APPEND-FIRST as an Originated Credit Movement (the lump sum ENTERS the disbursement
/// account; the cash leg is the substrate-owned settlement saga's gated step — ADR-PC-032 slot 5).
/// </summary>
/// <param name="LoanId">The loan stream/aggregate id (caller-supplied).</param>
/// <param name="PrincipalCents">The disbursed capital (lump sum), in integer cents.</param>
/// <param name="ProductId">The product code the rate sheet prices, e.g. <c>cp_pt_general_36m</c>.</param>
/// <param name="Role">The pricing role resolved from the loan origin, e.g. <c>standard</c>.</param>
/// <param name="TermMonths">The number of monthly installments the loan amortizes over.</param>
/// <param name="StartDate">The disbursement date — the schedule anchor.</param>
/// <param name="Purpose">The loan purpose category (e.g. <c>general</c>) — not PII.</param>
/// <param name="DisbursementAccountRef">The OPAQUE account token the lump sum is credited to (a reference,
/// NOT an IBAN — ADR-PC-004 §P2).</param>
/// <param name="DisbursedAt">The instant the sheet is resolved as-of and the event's valid time;
/// host-stamped from the wall clock when omitted (the decider stays pure — ADR-PC-010 §P5).</param>
/// <param name="EarlyRepaymentCommissionBps">The early-repayment commission the product charges, in basis
/// points — defaults to the PT consumer-credit statutory cap (50 bps).</param>
/// <param name="Actor">The acting principal recorded on the append (defaults to <c>ops:loan-officer</c>).</param>
public sealed record DisburseLoanRequest(
    Guid LoanId,
    long PrincipalCents,
    string ProductId,
    string Role,
    int TermMonths,
    DateOnly StartDate,
    string Purpose,
    string DisbursementAccountRef,
    DateTimeOffset? DisbursedAt = null,
    int EarlyRepaymentCommissionBps = 50,
    string? Actor = null);

/// <summary>Pay one scheduled installment (POST /v1/loans/{id}/installment): the engine derives the next
/// installment from the loan's schedule and folds it. Carries a mandatory <c>Idempotency-Key</c>
/// (ADR-PC-029 slot 4) — an at-least-once retry must not double-pay.</summary>
/// <param name="CollectionAccountRef">The OPAQUE account token the installment is collected from (a
/// reference, NOT an IBAN — ADR-PC-004 §P2).</param>
/// <param name="PaidAt">The instant the installment is paid; host-stamped when omitted.</param>
/// <param name="Actor">The acting principal recorded on the append (defaults to <c>ops:loan-officer</c>).</param>
public sealed record PayInstallmentRequest(
    string CollectionAccountRef,
    DateTimeOffset? PaidAt = null,
    string? Actor = null);

/// <summary>Repay a loan early (POST /v1/loans/{id}/early-repayment): a partial or full prepayment of the
/// outstanding capital plus the legally-capped early-repayment commission. A FULL repayment settles the
/// loan. Carries a mandatory <c>Idempotency-Key</c> (ADR-PC-029 slot 4) — a partial repayment is repeatable,
/// so a retry must dedupe rather than repay twice.</summary>
/// <param name="RepaymentAmountCents">The capital to repay early, in integer cents (equal to the
/// outstanding balance ⇒ a full repayment that settles the loan).</param>
/// <param name="RepaymentAccountRef">The OPAQUE account token the repayment + commission is collected from
/// (a reference, NOT an IBAN — ADR-PC-004 §P2).</param>
/// <param name="RepaidAt">The instant the repayment fires; host-stamped when omitted.</param>
/// <param name="Actor">The acting principal recorded on the append (defaults to <c>ops:loan-officer</c>).</param>
public sealed record RepayEarlyRequest(
    long RepaymentAmountCents,
    string RepaymentAccountRef,
    DateTimeOffset? RepaidAt = null,
    string? Actor = null);

/// <summary>Write off a defaulted loan (POST /v1/loans/{id}/write-off): the engine RECORDS the write-off as
/// an unrecoverable loss — it does not run collections (ADR-PC-030 §P1). NO money moves. Carries a mandatory
/// <c>Idempotency-Key</c> (ADR-PC-029 slot 4).</summary>
/// <param name="WriteOffReason">A stable, non-PII reason code (e.g. <c>DEFAULT_UNRECOVERABLE</c>).</param>
/// <param name="WrittenOffAt">The instant the write-off fires; host-stamped when omitted.</param>
/// <param name="Actor">The acting principal recorded on the append (defaults to <c>ops:loan-officer</c>).</param>
public sealed record WriteOffLoanRequest(
    string WriteOffReason,
    DateTimeOffset? WrittenOffAt = null,
    string? Actor = null);

/// <summary>GDPR Article 17 right-to-be-forgotten on a loan (POST /v1/loans/{id}/erase-personal-data):
/// record the erasure fact (the host has ALREADY crypto-shredded the subject's key — ADR-PC-004 §P3).
/// Carries a mandatory <c>Idempotency-Key</c> (ADR-PC-029 slot 4) — key destruction is irreversible.</summary>
/// <param name="SubjectPseudonym">A salted one-way hash of the data-subject id — an OPAQUE reference, NEVER
/// the raw subject id (ADR-PC-004 §P2).</param>
/// <param name="ErasureReason">A stable machine code (e.g. <c>GDPR_ARTICLE_17</c>) — never PII.</param>
/// <param name="ErasedAt">The instant erasure took effect; host-stamped when omitted.</param>
/// <param name="Actor">The acting principal recorded on the append (defaults to <c>ops:dpo</c>).</param>
public sealed record ErasePersonalDataRequest(
    string SubjectPseudonym,
    string ErasureReason,
    DateTimeOffset? ErasedAt = null,
    string? Actor = null);

/// <summary>A loan command outcome: the loan id, its folded lifecycle status, and the commit sequence the
/// append reached (ADR-IC-005 §P3 read-your-writes token). Carries no PII — structural facts only.</summary>
public sealed record LoanCommandResponse(Guid LoanId, string Status, long CommitSequence);

/// <summary>The denormalized loan read view (GET /v1/loans/{id}), folded from the event stream. All money
/// fields are integer cents on the wire (ADR-PC-010 §P1); no PII — the disbursement account is an opaque
/// reference (ADR-PC-004 §P2).</summary>
public sealed record LoanResponse(
    Guid LoanId,
    long PrincipalCents,
    int TanBasisPoints,
    string RateSheetVersionId,
    int TermMonths,
    long InstallmentAmountCents,
    DateOnly StartDate,
    string Purpose,
    string ProductCode,
    long OutstandingBalanceCents,
    int InstallmentsPaid,
    long TotalInterestPaidCents,
    long TotalCapitalRepaidCents,
    long TotalCommissionChargedCents,
    long WrittenOffAmountCents,
    string Status);
