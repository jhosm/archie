using Babelstone.Engine;
using Babelstone.Engine.Hosting;
using Babelstone.Families.PersonalLoan;
using Babelstone.Lifecycle;
using Xunit;

namespace Babelstone.Families.PersonalLoan.Lifecycle.Tests;

/// <summary>
/// The dispatch-mapping FITNESS FUNCTION for the loan installment (ADR-PC-036 §Decision 7 — "share the
/// dispatch mapping with <c>SimulationRuntime</c> so the forecast is a fitness function"), the recurring
/// sibling of the term-deposit
/// <c>LifecycleDispatchFitnessTests</c>. In plain terms: a forecast milestone for "installment N of loan L
/// due on D" and the production command the driver fires for the same occurrence must agree on WHAT fires
/// — command kind, the number-pinned occurrence key, the due instant, and the canonical dispatch id — or
/// the forecast is lying about production. The production side is built through the live
/// <see cref="InstallmentRule"/> (the A4b path, gate healthy); the forecast side through
/// <see cref="PersonalLoanLifecycleDispatch.InstallmentMilestone"/>, the identity-stamped
/// <see cref="LifecycleMilestone"/> a loan forward schedule carries.
/// </summary>
public sealed class LifecycleDispatchFitnessTests
{
    // The engine installment endpoint's own derivation kind (LoansEndpoints.PayInstallmentCommandKind) —
    // the external anchor, quoted as a literal so the convergence assertions are not circular.
    private const string EnginePayInstallmentKind = "pay_installment";

    [Fact]
    public async Task The_forecast_installment_milestone_and_the_production_command_agree_on_the_occurrence()
    {
        var loan = Guid.NewGuid();
        var dueDate = new DateOnly(2026, 9, 28);
        const long installmentNumber = 7;

        // PRODUCTION: the driver's real recurring rule over the installment calendar (the A4b path; the
        // LCD-2 gate healthy — the gate holds WHETHER an occurrence surfaces, never WHAT it fires as).
        var rule = new InstallmentRule(
            new SingleLoanStore(Loan(loan, installmentNumber, dueDate)), new FakeSettlementHealthProbe());
        var decision = Assert.Single(await rule.EvaluateAsync(dueDate));

        // FORECAST: the identity-stamped milestone a loan forward schedule carries for the SAME occurrence.
        var milestone = PersonalLoanLifecycleDispatch.InstallmentMilestone(
            installmentNumber, dueDate, (_, _) => Task.CompletedTask);

        // The SAME occurrence identity — kind and the stable installment NUMBER — on both sides.
        Assert.Equal(decision.CommandKind, milestone.CommandKind);
        Assert.Equal(decision.OccurrenceKey, milestone.OccurrenceKey);

        // The SAME due instant: the milestone falls due exactly when the production body says the
        // business valid_time is (paid_at = the due date's UTC midnight).
        Assert.Equal(milestone.DueAt, Assert.IsType<DateTimeOffset>(decision.Body["paid_at"]));

        // The SAME canonical dispatch id (LCD-1): the forecast identity derives byte-for-byte the
        // number-pinned key production presents — and both equal the engine's own derivation kind.
        Assert.NotNull(milestone.CommandKind);
        Assert.NotNull(milestone.OccurrenceKey);
        Assert.Equal(
            LifecycleCommandKey.Derive(loan, milestone.CommandKind, milestone.OccurrenceKey.Value),
            LifecycleDispatchId.Of(decision));
        Assert.Equal(EnginePayInstallmentKind, milestone.CommandKind);
    }

    // --- helpers ---

    private static readonly JsonStateSerializer<LoanPosition> Codec = new();

    private static InstallmentCalendarReadModelRow Loan(Guid id, long nextNumber, DateOnly nextDue)
    {
        var position = LoanPosition.Empty with { LoanId = id, DisbursementAccountRef = "acct-collect-001" };
        return new InstallmentCalendarReadModelRow(
            StreamId: id,
            Sor: "engine",
            FirstInstallmentDate: nextDue.AddMonths(-(int)(nextNumber - 1)),
            TermMonths: 12,
            InstallmentAmountCents: 10_000,
            InstallmentsPaid: (int)nextNumber - 1,
            NextInstallmentNumber: (int)nextNumber,
            NextDueDate: nextDue,
            Detail: Codec.Serialize(position),
            LastSequence: 1,
            LastUpdated: default);
    }

    private sealed class SingleLoanStore(params InstallmentCalendarReadModelRow[] rows)
        : IInstallmentCalendarReadModelStore
    {
        public Task<IReadOnlyList<InstallmentCalendarReadModelRow>> ListByDueDateAsync(
            DateOnly fromInclusive, DateOnly toExclusive, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<InstallmentCalendarReadModelRow>>(
                rows.Where(r => r.NextDueDate is { } due && due >= fromInclusive && due < toExclusive).ToList());

        public Task UpsertAsync(InstallmentCalendarReadModelRow row, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<InstallmentCalendarReadModelRow?> GetAsync(Guid streamId, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task TruncateAsync(CancellationToken ct = default) => throw new NotSupportedException();
    }
}
