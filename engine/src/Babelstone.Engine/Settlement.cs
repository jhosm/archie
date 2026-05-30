using Babelstone.FinancialTypes;

namespace Babelstone.Engine;

/// <summary>
/// The money-movement leg of a lifecycle event: a debit or a credit against a
/// (legacy) current account (ADR-PC-016 §Payload). A constitution debits the
/// principal from the funding account; a maturity credits the payout. Amounts are
/// <see cref="Money"/> (integer cents); the engine never expresses money as a float.
/// </summary>
public sealed record SettlementInstruction(
    Guid AggregateId,
    SettlementDirection Direction,
    Money Amount,
    string Account,
    string Reason);

/// <summary>Debit (engine → legacy: take funds) or credit (legacy: receive funds).</summary>
public enum SettlementDirection
{
    Debit,
    Credit,
}

/// <summary>
/// The legacy-settlement seam (ADR-PC-016). A decider calls this to move money on the
/// legacy core during a lifecycle transition; the engine owns only the <i>port</i>, the
/// host wires the <i>adapter</i>. For the walking skeleton (E.3) the adapter is an
/// in-memory stub; the WireMock-backed SOAP stub is H.2 (the constitution saga) and the
/// real ACL is DEF-1 (gated on the Epic-0.6 legacy inventory). A debit is conditional —
/// legacy may refuse for insufficient funds (a throw); a credit legacy always accepts
/// (ADR-PC-016 §Semantics). An adapter signals a refused debit by <b>throwing</b> — never by
/// returning a completed <see cref="Task"/>: a decider settles before it appends, so swallowing
/// a downstream fault into success would let a constitution proceed without its money leg.
/// </summary>
public interface ISettlementPort
{
    Task SettleAsync(SettlementInstruction instruction, CancellationToken ct = default);
}
