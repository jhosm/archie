using Babelstone.Engine;
using Babelstone.Engine.Hosting;
using Babelstone.Families.PersonalLoan;
using Babelstone.Lifecycle;
using Xunit;

namespace Babelstone.Lifecycle.Tests;

/// <summary>
/// Tests for <see cref="InstallmentRule"/> — the personal-loan family's recurring lifecycle-command rule
/// (ADR-PC-036 §Decision 2/3/5; bd babelstone-6cpq.9). They drive the rule over a fake installment-calendar
/// read-model store (no live engine / no DB) and assert the recurring-installment safety the whole design
/// rests on:
/// <list type="bullet">
/// <item>an Active loan with occurrence N next-due fires ONCE, under the server-derived key the engine
/// installment endpoint also derives — <c>("pay_installment", N)</c>;</item>
/// <item>a RE-TICK of occurrence N re-derives the SAME number-pinned (E1) key and appends NO second money leg
/// (the dispatch ledger absorbs it; <c>command_dedup</c> is the engine backstop);</item>
/// <item>the driver advances to N+1 ONLY after N is recorded paid (the calendar fold's next-unpaid pointer
/// advances on the <c>LoanInstallmentPaid</c> event), and N+1 carries a DISTINCT id;</item>
/// <item>the decision carries the loan's own collection account (recovered from the row's detail) and its due
/// date, and presents NO SCA principal (the installment route is not step-up-gated);</item>
/// <item>an installment not yet due is not fired.</item>
/// </list>
/// </summary>
public sealed class InstallmentRuleTests
{
    private static readonly DateOnly Today = new(2026, 6, 28);
    private const string CollectionAccount = "acct-collect-001";

    // The stable command-kind the ENGINE installment endpoint derives its idempotency key under
    // (LoansEndpoints.PayInstallmentCommandKind). The driver must converge on it, so the key cross-checks use
    // this literal rather than the rule's own constant (which would make the assertion circular).
    private const string EnginePayInstallmentKind = "pay_installment";

    [Fact]
    public async Task An_active_loan_with_a_due_installment_fires_once_under_the_number_pinned_key()
    {
        var loan = Guid.NewGuid();
        var sink = new RecordingSink();
        var pass = NewPass(sink, new InstallmentRule(new FakeInstallmentStore(Loan(loan, nextNumber: 1, nextDue: Today))));

        var first = await pass.RunOnceAsync(Today);
        var second = await pass.RunOnceAsync(Today);

        Assert.Single(first);
        Assert.Empty(second);

        Assert.Single(sink.Dispatched);
        Assert.Equal("pay_installment", first[0].CommandKind);
        Assert.Equal(1, first[0].OccurrenceKey);
        Assert.Equal(LifecycleCommandKey.Derive(loan, EnginePayInstallmentKind, 1), first[0].CommandId);
    }

    [Fact]
    public async Task A_retick_of_occurrence_N_reuses_the_E1_key_and_appends_no_second_leg()
    {
        var loan = Guid.NewGuid();
        // Occurrence 1 stays the next-unpaid across both passes — its LoanInstallmentPaid event has not landed,
        // so the calendar keeps surfacing N. A repeated PayInstallment is legal from Active, so ALL safety here
        // rests on the number-pinned key deduping the re-tick.
        var sink = new RecordingSink();
        var pass = NewPass(sink, new InstallmentRule(new FakeInstallmentStore(Loan(loan, nextNumber: 1, nextDue: Today))));

        var first = await pass.RunOnceAsync(Today);
        var retick = await pass.RunOnceAsync(Today);

        Assert.Single(first);
        Assert.Empty(retick); // re-derived to the SAME E1 key → deduped, no second money leg
        Assert.Single(sink.Dispatched);
        Assert.Equal(LifecycleCommandKey.Derive(loan, EnginePayInstallmentKind, 1), sink.Dispatched[0].CommandId);
    }

    [Fact]
    public async Task The_driver_advances_to_N_plus_1_only_after_N_is_recorded_paid()
    {
        var loan = Guid.NewGuid();
        var store = new FakeInstallmentStore(Loan(loan, nextNumber: 1, nextDue: Today));
        var sink = new RecordingSink();
        var pass = NewPass(sink, new InstallmentRule(store));

        var firstTick = await pass.RunOnceAsync(Today);
        // Occurrence 1's LoanInstallmentPaid lands → the calendar fold advances the next-unpaid to occurrence 2.
        store.Replace(Loan(loan, nextNumber: 2, nextDue: Today));
        var secondTick = await pass.RunOnceAsync(Today);

        Assert.Equal(1, Assert.Single(firstTick).OccurrenceKey);
        Assert.Equal(2, Assert.Single(secondTick).OccurrenceKey);
        Assert.Equal(2, sink.Dispatched.Count);

        // N+1 carries a DISTINCT number-pinned id — a new occurrence, not a retry of N.
        Assert.Equal(LifecycleCommandKey.Derive(loan, EnginePayInstallmentKind, 2), secondTick[0].CommandId);
        Assert.NotEqual(sink.Dispatched[0].CommandId, sink.Dispatched[1].CommandId);
    }

    [Fact]
    public async Task The_decision_carries_the_loan_collection_account_due_date_and_no_sca_principal()
    {
        var loan = Guid.NewGuid();
        var rule = new InstallmentRule(new FakeInstallmentStore(Loan(loan, nextNumber: 3, nextDue: Today)));

        var decision = Assert.Single(await rule.EvaluateAsync(Today));

        Assert.Equal($"/v1/loans/{loan:D}/installment", decision.RequestPath);
        Assert.Equal(3, decision.OccurrenceKey);
        Assert.Equal(Today, decision.DueAt);
        // The collection account is the loan's own disbursement-account reference, recovered from the row's
        // serialized detail body.
        Assert.Equal(CollectionAccount, decision.Body["collection_account_ref"] as string);
        // paid_at rides as the due date's UTC midnight, the engine's valid_time for a late/backfilled firing.
        Assert.Equal(
            new DateTimeOffset(Today.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero),
            Assert.IsType<DateTimeOffset>(decision.Body["paid_at"]));
        // The loan installment route derives its key server-side and is NOT SCA-step-up-gated.
        Assert.Null(decision.ServicePrincipalScope);
    }

    [Fact]
    public async Task A_not_yet_due_installment_is_not_fired()
    {
        var loan = Guid.NewGuid();
        var rule = new InstallmentRule(new FakeInstallmentStore(Loan(loan, nextNumber: 1, nextDue: Today.AddDays(1))));

        Assert.Empty(await rule.EvaluateAsync(Today));
    }

    // --- helpers ---

    private static readonly JsonStateSerializer<LoanPosition> Codec = new();

    private static LifecycleSchedulePass NewPass(ILifecycleCommandSink sink, params ILifecycleCommandRule[] rules) =>
        new(rules, new LifecycleDispatchLedger(), sink);

    private static InstallmentCalendarReadModelRow Loan(Guid id, int nextNumber, DateOnly nextDue)
    {
        // The row's detail body is the serialized LoanPosition the read-model runner writes, carrying the
        // loan's disbursement-account reference the rule reuses as the collection account.
        var position = LoanPosition.Empty with { LoanId = id, DisbursementAccountRef = CollectionAccount };
        return new InstallmentCalendarReadModelRow(
            StreamId: id,
            Sor: "engine",
            FirstInstallmentDate: nextDue.AddMonths(-(nextNumber - 1)),
            TermMonths: 12,
            InstallmentAmountCents: 10_000,
            InstallmentsPaid: nextNumber - 1,
            NextInstallmentNumber: nextNumber,
            NextDueDate: nextDue,
            Detail: Codec.Serialize(position),
            LastSequence: 1,
            LastUpdated: default);
    }

    /// <summary>A fake installment-calendar store that honours the half-open [from, to) due-date window the
    /// rule scans (so "not yet due" is genuinely excluded), supports swapping its rows to simulate the calendar
    /// advancing once an installment is paid, and throws for the reads the rule never makes.</summary>
    private sealed class FakeInstallmentStore(params InstallmentCalendarReadModelRow[] rows)
        : IInstallmentCalendarReadModelStore
    {
        private InstallmentCalendarReadModelRow[] _rows = rows;

        public void Replace(params InstallmentCalendarReadModelRow[] next) => _rows = next;

        public Task<IReadOnlyList<InstallmentCalendarReadModelRow>> ListByDueDateAsync(
            DateOnly fromInclusive, DateOnly toExclusive, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<InstallmentCalendarReadModelRow>>(
                _rows.Where(r => r.NextDueDate is { } due && due >= fromInclusive && due < toExclusive)
                    .OrderBy(r => r.NextDueDate).ThenBy(r => r.StreamId).ToList());

        public Task UpsertAsync(InstallmentCalendarReadModelRow row, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<InstallmentCalendarReadModelRow?> GetAsync(Guid streamId, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task TruncateAsync(CancellationToken ct = default) => throw new NotSupportedException();
    }

    /// <summary>Records every command the pass POSTs through it (the decision + the derived command id).</summary>
    private sealed class RecordingSink : ILifecycleCommandSink
    {
        private readonly List<(LifecycleCommandDecision Decision, Guid CommandId)> _dispatched = [];

        public IReadOnlyList<(LifecycleCommandDecision Decision, Guid CommandId)> Dispatched => _dispatched;

        public Task DispatchAsync(LifecycleCommandDecision decision, Guid commandId, CancellationToken ct = default)
        {
            _dispatched.Add((decision, commandId));
            return Task.CompletedTask;
        }
    }
}
