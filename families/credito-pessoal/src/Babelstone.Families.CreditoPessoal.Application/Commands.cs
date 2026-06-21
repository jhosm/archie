namespace Babelstone.Families.CreditoPessoal.Application;

// The credito_pessoal command surface (ADR-PC-021). A command carries only per-loan facts; the pinned
// pack + its primitive bindings are engine-instance configuration held by the service (ADR-PC-009
// per-instance pinning). The resolved TAN and rate_sheet_version_id are NOT command inputs — the
// service resolves them at constitution from the rate sheet (ADR-PC-008 §P3) and stamps them onto the
// event. ORIGINATION stays UPSTREAM (ADR-PC-030 §P1 / ADR-PC-024): the loan arrives ALREADY-APPROVED
// and ALREADY-PRICED; the engine never models solvency / CRC / KYC / scoring — it disburses and amortizes.

/// <summary>
/// A resolved commercial-eligibility verdict the constitution saga gathered upstream and passes on the
/// disbursement command (ADR-PC-024 §1–§2). It asserts a <i>fact</i> — "an upstream authority evaluated
/// this predicate for this customer at <see cref="EvaluatedAt"/> and it is <see cref="Satisfied"/> / not."
/// The MEANING of each predicate is entirely upstream/pack-owned; the engine treats the verdict as OPAQUE
/// and never re-evaluates it. The triple carries NO PII: <see cref="EvidenceRef"/> is a resolvable
/// reference, never identity data (ADR-PC-024 §1). Mirrors the term-deposit type one-for-one.
/// </summary>
public sealed record PreconditionVerdict(
    bool Satisfied,
    string EvidenceRef,
    DateTimeOffset EvaluatedAt);

/// <summary>
/// Disburse a personal loan: the principal, term, and pricing inputs fixed at constitution. The loan was
/// ALREADY approved and priced upstream (ADR-PC-030 / ADR-PC-024) — this command carries the already-decided
/// facts; the engine resolves the rate sheet for lineage, computes the amortization schedule, and disburses.
/// </summary>
/// <param name="LoanId">The loan stream/aggregate id.</param>
/// <param name="PrincipalCents">The disbursed capital (lump sum), in integer cents.</param>
/// <param name="ProductId">The product code the rate sheet prices, e.g. <c>cp_pt_general_36m</c>.</param>
/// <param name="Role">The pricing role resolved from the loan origin, e.g. <c>standard</c>.</param>
/// <param name="TermMonths">The number of monthly installments the loan amortizes over.</param>
/// <param name="StartDate">The disbursement date — the schedule anchor.</param>
/// <param name="DisbursedAt">The instant the sheet is resolved as-of and the event's valid time.</param>
/// <param name="Finalidade">The loan purpose/<i>finalidade</i> category (e.g. <c>general</c>,
/// <c>education</c>) — selects the legal TAEG ceiling bucket (research/credito-pessoal/02 §2). Not PII.</param>
/// <param name="DisbursementAccount">The opaque account token the lump sum is credited to (a reference,
/// NOT an IBAN — ADR-PC-004 §P2).</param>
/// <param name="Actor">The acting principal recorded on the append.</param>
/// <param name="EarlyRepaymentCommissionBps">The early-repayment commission the product charges, in basis
/// points — PINNED at constitution (ADR-PC-009). Defaults to the PT consumer-credit statutory cap (50 bps).</param>
/// <param name="Preconditions">The resolved commercial-eligibility verdicts the saga gathered upstream
/// (ADR-PC-024), keyed by the engine's closed verdict-key taxonomy. The decider refuses the disbursement
/// when a verdict the product's <c>required_preconditions</c> demands is absent or <c>Satisfied == false</c>
/// — a PURE function of these verdicts, with no in-engine evaluation. Defaults to empty.</param>
/// <param name="CommandId">The caller's deterministic command id (ADR-PC-029 slot 4) — the append is
/// idempotent on it. Nullable for direct in-process callers (family unit tests).</param>
public sealed record DisburseLoanCommand(
    Guid LoanId,
    long PrincipalCents,
    string ProductId,
    string Role,
    int TermMonths,
    DateOnly StartDate,
    DateTimeOffset DisbursedAt,
    string Finalidade,
    string DisbursementAccount,
    string Actor,
    int EarlyRepaymentCommissionBps = 50,
    IReadOnlyDictionary<string, PreconditionVerdict>? Preconditions = null,
    Guid? CommandId = null);

/// <summary>Pay one scheduled installment: record the next installment's interest+capital split and reduce
/// the outstanding balance (<c>P(t) = J(t) + A(t)</c>, fin-math §3). The installment is derived by the
/// service from the loan's schedule and the number of installments already paid — not a command input
/// (the engine owns the schedule). Triggered MANUALLY here, exactly as a deposit coupon is.</summary>
/// <param name="LoanId">The loan stream id.</param>
/// <param name="PaidAt">The instant the installment is paid; its DATE is recorded on the event.</param>
/// <param name="CollectionAccount">The opaque account token the installment is collected from (a
/// reference, NOT an IBAN — ADR-PC-004 §P2). Settled via the débito-direto leg.</param>
/// <param name="Actor">The acting principal recorded on the append.</param>
/// <param name="CommandId">The ingress idempotency key (ADR-PC-029 slot 4): the append dedupes on it, so
/// an at-least-once retry of the SAME installment returns the original outcome rather than double-paying.</param>
public sealed record PayInstallmentCommand(
    Guid LoanId,
    DateTimeOffset PaidAt,
    string CollectionAccount,
    string Actor,
    Guid CommandId);

/// <summary>Repay a loan early (<i>reembolso antecipado</i>, fin-math §7.5): a partial or full prepayment
/// of the outstanding capital, plus the LEGALLY-CAPPED early-repayment commission. The commission rate is
/// resolved by the service from the gates PINNED on the loan at constitution and the statutory cap for the
/// remaining-term band (0.50% &gt;1y, 0.25% ≤1y); the pure decider takes the resolved figures as inputs.
/// A FULL repayment (whole outstanding balance) drives the balance to zero and the loan settles.</summary>
/// <param name="LoanId">The loan stream id.</param>
/// <param name="RepaymentAmountCents">The capital to repay early, in integer cents. Equal to the
/// outstanding balance ⇒ a full repayment (the loan settles); less ⇒ a partial repayment (stays Active).</param>
/// <param name="RepaidAt">The instant the repayment fires; its DATE is recorded and the remaining-term
/// cap band is selected against it. Passed as an INPUT so the decision stays pure (no clock in the decider).</param>
/// <param name="RepaymentAccount">The opaque account token the repayment + commission is collected from
/// (a reference, NOT an IBAN — ADR-PC-004 §P2).</param>
/// <param name="Actor">The acting principal recorded on the append.</param>
/// <param name="CommandId">The ingress idempotency key (ADR-PC-029 slot 4): MANDATORY — a partial
/// repayment is REPEATABLE (leaves the loan Active), so a non-idempotent retry would repay twice.</param>
public sealed record RepayEarlyCommand(
    Guid LoanId,
    long RepaymentAmountCents,
    DateTimeOffset RepaidAt,
    string RepaymentAccount,
    string Actor,
    Guid CommandId);

/// <summary>Write off a defaulted loan as an unrecoverable loss (ADR-PC-030 §P1 item 4): the engine RECORDS
/// the write-off as a structural audit fact — it does NOT run the collections procedure (PARI/PERSI
/// enforcement is upstream; the engine records resulting state only).</summary>
/// <param name="LoanId">The loan stream id.</param>
/// <param name="WrittenOffAt">The instant the write-off fires; its DATE is recorded on the event.</param>
/// <param name="WriteOffReason">A stable, non-PII reason code (e.g. <c>DEFAULT_UNRECOVERABLE</c>).</param>
/// <param name="Actor">The acting principal recorded on the append.</param>
/// <param name="CommandId">The ingress idempotency key (ADR-PC-029 slot 4).</param>
public sealed record WriteOffLoanCommand(
    Guid LoanId,
    DateTimeOffset WrittenOffAt,
    string WriteOffReason,
    string Actor,
    Guid CommandId);

/// <summary>
/// Record the GDPR Article 17 erasure fact on a loan (ADR-PC-004 §P3): append
/// <see cref="PersonalDataErasureRequested"/> so the loan folds to <c>Erased</c>. The actual
/// crypto-shredding of the subject's key runs in the impure HOST shell BEFORE this command is issued, so
/// this command carries ONLY structural facts: the loan id, a salted one-way subject pseudonym (never the
/// raw subject id), the erasure date, and a reason code. Mirrors the term-deposit erasure command.
/// </summary>
/// <param name="LoanId">The loan stream id.</param>
/// <param name="SubjectPseudonym">A salted one-way hash of the data-subject id — an opaque reference,
/// NEVER the raw subject id (ADR-IC-016 §8 / ADR-PC-004 §P2).</param>
/// <param name="ErasedAt">The instant erasure took effect; its DATE is recorded (audit lineage).</param>
/// <param name="ErasureReason">A stable machine code (e.g. <c>GDPR_ARTICLE_17</c>) — never PII.</param>
/// <param name="Actor">The acting principal recorded on the append.</param>
/// <param name="CommandId">The ingress idempotency key (ADR-PC-029 slot 4): MANDATORY — key destruction
/// is irreversible, so a non-idempotent retry must be impossible.</param>
public sealed record ErasePersonalDataCommand(
    Guid LoanId,
    string SubjectPseudonym,
    DateTimeOffset ErasedAt,
    string ErasureReason,
    string Actor,
    Guid CommandId);
