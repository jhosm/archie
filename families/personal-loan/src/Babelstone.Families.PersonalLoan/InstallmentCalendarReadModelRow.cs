using Babelstone.EventStore;

namespace Babelstone.Families.PersonalLoan;

/// <summary>
/// One denormalized CQRS read-model row for a personal loan's FORWARD installment calendar (ADR-IC-005):
/// the flat, query-optimized read side that backs the "loans with an installment due in [from, to)"
/// range scan without folding every stream. This is the FAMILY-OWNED row shape — the personal_loan family
/// names its own typed query columns here, NOT the engine spine: the spine sees it only through
/// <see cref="IReadModelRow"/> (stream id + the §P2 sequence guard + the opaque <see cref="Detail"/>
/// body), so adding a non-loan family is zero generic-engine diff (ADR-PC-021 §D2/§P2). The matching
/// <c>read_model.installment_calendar</c> table and the due-date range scan live in this family's
/// <c>PostgresInstallmentCalendarReadModelStore</c> (the impure Application project) — the same split as
/// <c>read_model.deposits</c> / <c>DepositReadModelRow</c> in the term-deposit family.
/// </summary>
/// <remarks>
/// <para>
/// The forward NEXT-unpaid occurrence is surfaced as the nullable
/// <see cref="NextInstallmentNumber"/> / <see cref="NextDueDate"/> pair, denormalized for a point read
/// and (via <see cref="NextDueDate"/>) the range-scan dimension a reminder/lifecycle-command path filters
/// on. BOTH go <see langword="null"/> when there is no next occurrence — a terminal loan (settled,
/// written-off, erased, failed) or a fully-paid one — so an exhausted or closed loan never appears in the
/// "installments due in [from, to)" scan. The producing <see cref="Babelstone.Engine.ReadModelRunner{TState,TRow}"/> folds
/// the SAME <c>LoanPosition</c> the live aggregate fold produces, so the mapper projects facts already in
/// <c>fold.State</c> (still pure, still cents-native — ADR-PC-010 §P1/§P5).
/// </para>
/// <para>
/// <see cref="LastSequence"/> is the ADR-IC-005 §P2 monotonicity guard. This engine's event store has no
/// Redpanda offset (events drain per stream, no cluster-wide order), so the §P2 <c>last_event_offset</c>
/// is realised as the per-stream <c>sequence_number</c> of the producing event — a re-delivered or
/// out-of-order event whose sequence is at or below the stored row's is dropped by the UPSERT guard,
/// making the at-least-once drainer safe. <see cref="LastUpdated"/> (ADR-IC-005 §P3) is RUNTIME-SUPPLIED
/// from the producing event's transaction_time, never the SQL clock, so a cold rebuild (TRUNCATE +
/// re-fold) reproduces the row byte-for-byte (ADR-PC-010 §P5). <see cref="Detail"/> is the serialized
/// structural <c>LoanPosition</c> the runner re-hydrates to continue its accumulating fold across events.
/// </para>
/// <para>
/// <see cref="Sor"/> is the ADR-PC-018 §6.2 routing-truth column: <c>engine</c> for every
/// engine-materialised loan. No PII lives in this row (ADR-PC-004 §P2) — structural schedule facts and
/// opaque references only; all money is integer cents (ADR-PC-010 §P1).
/// </para>
/// </remarks>
public sealed record InstallmentCalendarReadModelRow(
    Guid StreamId,
    string Sor,
    DateOnly FirstInstallmentDate,
    int TermMonths,
    long InstallmentAmountCents,
    int InstallmentsPaid,
    int? NextInstallmentNumber,
    DateOnly? NextDueDate,
    ReadOnlyMemory<byte> Detail,
    long LastSequence,
    DateTimeOffset LastUpdated) : IReadModelRow;
