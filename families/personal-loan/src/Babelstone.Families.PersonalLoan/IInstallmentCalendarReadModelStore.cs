using Babelstone.EventStore;

namespace Babelstone.Families.PersonalLoan;

/// <summary>
/// The personal_loan family's installment-calendar read-model store (ADR-IC-005): the generic spine
/// primitive (<see cref="IReadModelStore{TRow}"/> — the UPSERT-with-§P2-guard write, the point lookup, and
/// the truncate-and-refold rebuild) PLUS this family's own range-scan read. The loan-shaped table and the
/// due-date query are OWNED HERE, in the family layer, not in the engine spine — the spine knows the row
/// only through <see cref="IReadModelRow"/>, so adding a non-loan family is zero generic-engine diff
/// (ADR-PC-021 §D2/§P2). The read-side mirror of how the family layers its typed projection over the
/// generic <c>IProjectionStorage</c>, and the closed-end-asset analogue of the term-deposit family's
/// <c>IDepositReadModelStore</c>.
/// </summary>
public interface IInstallmentCalendarReadModelStore : IReadModelStore<InstallmentCalendarReadModelRow>
{
    /// <summary>
    /// The range-scan read: every loan whose forward next-unpaid installment's
    /// <see cref="InstallmentCalendarReadModelRow.NextDueDate"/> falls in the half-open <c>[from, to)</c>
    /// window, ordered by due date then by id (a deterministic, stable order). Backs the "loans with an
    /// installment due in [from, to)" query the downstream reminder / lifecycle-command driver
    /// (ADR-PC-036) range-scans, rather than folding every stream. A loan with no next occurrence (a
    /// terminal or fully-paid loan, both with a NULL <c>next_due_date</c>) is excluded by construction.
    /// Family-specific (a non-loan family has no installment schedule), so it lives on the family store,
    /// not the generic spine primitive.
    /// </summary>
    Task<IReadOnlyList<InstallmentCalendarReadModelRow>> ListByDueDateAsync(
        DateOnly fromInclusive, DateOnly toExclusive, CancellationToken ct = default);
}
