using Babelstone.FinancialTypes;

namespace Babelstone.Engine;

/// <summary>
/// One recorded change of value against a single engine-owned account — the spine's atom for moving
/// money (ADR-PC-032). In plain English: every place babelstone moves money (a deposit takes principal
/// in, a loan pays principal out, a maturity pays out, an installment is collected) becomes the SAME
/// thing — one <see cref="Movement"/>, recorded on the event that already states the fact, written
/// first, so the cash move is a safe retryable consequence rather than an un-recoverable precondition.
/// </summary>
/// <remarks>
/// <para>
/// <b>Single-sided and family-agnostic.</b> A <see cref="Movement"/> is single-sided (one
/// <see cref="Direction"/> against one <see cref="AccountRef"/>) and names NO family (ADR-PC-032 slot 1
/// / slot 6): it carries an opaque <see cref="AccountRef"/>, a closed <see cref="MovementOperation"/>
/// code, and generic <see cref="Money"/> / <see cref="SettlementDirection"/> / <see cref="ValueDate"/> —
/// exactly the family-agnostic shape the spine carries, so <c>ENGINE_FAMILY_AGNOSTIC</c>
/// (ADR-PC-021) holds. It is NOT a balanced double-entry
/// pair: the engine records single-sided movements against its own account balances; posting them as a
/// balanced journal entry against a chart of accounts is the GL's job (ADR-PC-012), which the engine
/// never does.
/// </para>
/// <para>
/// <b>Carried inside the event, written append-first.</b> A <see cref="Movement"/> rides as data inside
/// the money-moving event's existing opaque payload — NO new <c>events</c>-table column and NO envelope
/// change (ADR-PC-001 / ADR-PC-010 column contract untouched). Because it rides the event, it is
/// written append-first inside the event's existing outbox transaction (ADR-IC-004): the FACT is durable
/// first, the cash leg is a downstream gated consequence (the settlement saga, a SEPARATE issue, not
/// built here). An event may carry MORE THAN ONE movement (a renewal is a rollover debit AND an interest
/// credit), so the carrier on a carrying event is <c>IReadOnlyList&lt;Movement&gt;</c>.
/// </para>
/// <para>
/// <b>No PII on the bus.</b> <see cref="AccountRef"/> is an OPAQUE reference the engine resolves
/// internally, NEVER an IBAN / cleartext or ciphertext account identifier (ADR-PC-004): references
/// are allowed on the durable bus, PII is not.
/// </para>
/// </remarks>
/// <param name="AccountRef">The legacy-Core / engine-owned account the value moves against — an OPAQUE
/// token, a reference the engine resolves internally, NEVER PII (ADR-PC-004).</param>
/// <param name="Direction"><see cref="SettlementDirection.Debit"/> or <see cref="SettlementDirection.Credit"/>,
/// ALWAYS relative to <paramref name="AccountRef"/>: <c>Debit</c> = value leaves that account, <c>Credit</c>
/// = value enters it. Pinning direction to the named account is what kills the "a loan pays out yet was
/// coded Debit" wrinkle the old DTO left ambiguous (feature-design §2).</param>
/// <param name="Amount">The amount moved, as <see cref="Money"/> (integer cents; crosses from
/// <see cref="decimal"/> exactly once, ADR-PC-010). The engine never expresses money as a float.</param>
/// <param name="ValueDate">The economic date the value moves (<c>valid_time</c>), never wall-clock —
/// supplied by the decider, not read from a clock (ADR-PC-032 slot 1).</param>
/// <param name="Operation">Which money move this records — see <see cref="MovementOperation"/>.</param>
/// <param name="Origin">Who initiated the move and what the spine does next — see <see cref="MovementOrigin"/>.</param>
/// <param name="CommandId">The ADR-PC-029 append-idempotency <c>CommandId</c> the originating command
/// carried, threaded onto the movement for append idempotency + correlation (ADR-PC-032 slot 4). A
/// replayed command id returns the original <c>commit_sequence</c> with no second append, so the movement
/// it records is never double-written.</param>
public sealed record Movement(
    string AccountRef,
    SettlementDirection Direction,
    Money Amount,
    DateOnly ValueDate,
    MovementOperation Operation,
    MovementOrigin Origin,
    Guid CommandId);

/// <summary>
/// Who initiated a <see cref="Movement"/> and what the spine does next (ADR-PC-032 slot 2). A CLOSED set
/// of exactly two members — the engine either decided the money move or observed it already cleared;
/// there is no third posture.
/// </summary>
public enum MovementOrigin
{
    /// <summary>The decider decided the movement (a loan disbursement, a deposit maturity payout). The
    /// movement is recorded append-first; its cash leg is then EFFECTED on the legacy Core by the generic
    /// confirmation-gated settlement leg (ADR-PC-032 slot 5 / ADR-PC-016). Settlement is a CONSEQUENCE of
    /// a durable record, never a precondition of the append.</summary>
    Originated,

    /// <summary>An already-cleared movement (a card capture, a Core-side posting) arrived via the
    /// capture/settlement feed (ADR-PC-030). The engine RECORDS it and folds it into the balance;
    /// there is no cash leg to drive (the money already moved upstream). v1 has no Observed producer; the
    /// path is specified but not yet exercised (ADR-PC-032).</summary>
    Observed,
}

/// <summary>
/// The CLOSED engine-side operation code naming WHICH money move a <see cref="Movement"/> records
/// (ADR-PC-032 slot 1; hardened from the old free-string <c>Reason</c> to a closed type by
/// feature-design §2). An enum, not a free string: the settlement leg maps it to the ACL
/// <c>operation_type</c> half of the <c>(operation_type, saga_step_id, external_reference)</c>
/// idempotency key (ADR-IC-012), and a closed set keeps that mapping total. Family-agnostic: these
/// are GENERIC money-movement verbs (disburse, collect an installment, pay a maturity, …), not a
/// family-typed shape — each family chooses WHICH of these its lifecycle events carry, but names none of
/// its own here (ADR-PC-021). Widening the set is forward-only schema evolution (ADR-IC-002):
/// add a member at the END so existing ordinals are stable.
/// </summary>
public enum MovementOperation
{
    /// <summary>A loan disbursement — the lump-sum principal paid out at constitution (fin-math §4.1).</summary>
    Disburse,

    /// <summary>A loan installment collection — one scheduled amortization payment taken in.</summary>
    CollectInstallment,

    /// <summary>A deposit maturity payout — principal (and any at-maturity interest) returned at maturity.</summary>
    PayMaturity,

    /// <summary>A periodic deposit coupon payout — a scheduled interest payment (PERIODIC / ADVANCE variants).</summary>
    PayCoupon,

    /// <summary>An early-termination payout — the net proceeds of a deposit terminated before maturity.</summary>
    PayEarlyTermination,

    /// <summary>An early loan repayment — the borrower repays outstanding principal ahead of schedule.</summary>
    RepayEarly,

    /// <summary>The rollover debit leg of a deposit renewal — the matured principal re-debited into the
    /// renewed deposit (paired with an interest credit on the same renewal event).</summary>
    RolloverDebit,

    /// <summary>An overdraft-interest accrual — the fee charged against a demand account's drawn (negative)
    /// balance (ADR-PC-037 §D5), a Debit that makes the balance more negative. Added at the END so the
    /// existing ordinals are stable (forward-only schema evolution, ADR-IC-002).</summary>
    AccrueOverdraftInterest,
}
