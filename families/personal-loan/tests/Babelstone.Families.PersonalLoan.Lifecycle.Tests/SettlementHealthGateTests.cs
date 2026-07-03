using Babelstone.Engine;
using Babelstone.Engine.Hosting;
using Babelstone.Families.PersonalLoan;
using Babelstone.Lifecycle;
using Xunit;

namespace Babelstone.Families.PersonalLoan.Lifecycle.Tests;

/// <summary>
/// The LCD-2 settlement-health gate, Docker-free (ADR-PC-036 §Decision 4,
/// <c>LIFECYCLE_DRIVER_SETTLEMENT_HEALTH_GATE</c>; bd babelstone-6cpq.10). In plain terms: the engine
/// advances a loan's paid-count on the installment EVENT, not on settled CASH — so after an outage, a
/// catch-up could fire installment N+1 while N's cash is still stuck in human intervention. These tests
/// drive the REAL <see cref="InstallmentRule"/> + <see cref="LifecycleSchedulePass"/> over a fake calendar
/// and a fake settlement probe and prove the gate's whole contract:
/// <list type="bullet">
/// <item>N+1 is HELD while occurrence N's cash leg is parked in <c>HUMAN_INTERVENTION_REQUIRED</c>, and
/// RESUMES on the first pass after it settles;</item>
/// <item>an automated catch-up after an outage never advances the paid-count past collected cash — held
/// across every pass while parked, no matter how overdue the occurrence is;</item>
/// <item>the hold is per-instance (a healthy loan in the same pass still fires);</item>
/// <item>installment 1 is held while the loan's own disbursement leg is parked (same predicate, strictly
/// safer).</item>
/// </list>
/// The same held/resume walk against a REAL <c>saga_state</c> row (the orchestrator's actual schema and
/// state literals) is the integration twin, <c>SettlementHealthGateIntegrationTests</c>.
/// </summary>
public sealed class SettlementHealthGateTests
{
    private static readonly DateOnly Today = new(2026, 6, 28);
    private const string EnginePayInstallmentKind = "pay_installment";

    [Fact]
    public async Task N_plus_1_is_held_while_N_is_parked_and_resumes_once_settled()
    {
        var loan = Guid.NewGuid();
        var store = new FakeInstallmentStore(Loan(loan, nextNumber: 1, nextDue: Today));
        var probe = new FakeSettlementHealthProbe();
        var sink = new RecordingSink();
        var pass = NewPass(sink, new InstallmentRule(store, probe));

        // Occurrence 1 fires normally (no parked leg).
        var first = await pass.RunOnceAsync(Today);
        Assert.Equal(1, Assert.Single(first).OccurrenceKey);

        // N's LoanInstallmentPaid lands (the paid-count advanced), but its CASH leg parks in
        // HUMAN_INTERVENTION_REQUIRED — the settlement saga awaits an operator.
        store.Replace(Loan(loan, nextNumber: 2, nextDue: Today));
        probe.Park(loan);

        // The gate HOLDS N+1: the calendar surfaces occurrence 2 as due, but the rule refuses to
        // surface it while the instance's cash leg is parked (ADR-PC-036 §Decision 4).
        Assert.Empty(await pass.RunOnceAsync(Today));
        Assert.Single(sink.Dispatched);

        // The operator resolves the leg (HIR → SETTLEMENT_COMPLETED): the schedule RESUMES on the very
        // next pass — occurrence 2 fires under its own distinct number-pinned id.
        probe.Resolve(loan);
        var resumed = await pass.RunOnceAsync(Today);
        Assert.Equal(2, Assert.Single(resumed).OccurrenceKey);
        Assert.Equal(LifecycleCommandKey.Derive(loan, EnginePayInstallmentKind, 2), resumed[0].CommandId);
        Assert.Equal(2, sink.Dispatched.Count);
    }

    [Fact]
    public async Task Catch_up_after_an_outage_never_advances_the_paid_count_past_collected_cash()
    {
        var loan = Guid.NewGuid();
        // The outage scenario: occurrence 3 is long overdue (the driver was down), occurrence 2's cash
        // leg parked meanwhile. Backfill WANTS to fire 3 immediately — the gate must hold it anyway.
        var overdue = Today.AddDays(-45);
        var store = new FakeInstallmentStore(Loan(loan, nextNumber: 3, nextDue: overdue));
        var probe = new FakeSettlementHealthProbe();
        probe.Park(loan);
        var sink = new RecordingSink();
        var pass = NewPass(sink, new InstallmentRule(store, probe));

        // Pass after pass of automated catch-up: NOTHING fires while the leg is parked — the engine's
        // paid-count cannot outrun collected cash, no matter how many ticks elapse.
        Assert.Empty(await pass.RunOnceAsync(Today));
        Assert.Empty(await pass.RunOnceAsync(Today.AddDays(1)));
        Assert.Empty(await pass.RunOnceAsync(Today.AddDays(2)));
        Assert.Empty(sink.Dispatched);

        // Settled → the backfill proceeds, from exactly where the cash stands (occurrence 3, its own
        // OVERDUE due date as the business valid_time — correct backfill by construction, §S2).
        probe.Resolve(loan);
        var resumed = await pass.RunOnceAsync(Today.AddDays(3));
        Assert.Equal(3, Assert.Single(resumed).OccurrenceKey);
        Assert.Equal(overdue, resumed[0].DueAt);
    }

    [Fact]
    public async Task The_hold_is_per_instance_a_healthy_loan_still_fires_in_the_same_pass()
    {
        var parkedLoan = Guid.NewGuid();
        var healthyLoan = Guid.NewGuid();
        var store = new FakeInstallmentStore(
            Loan(parkedLoan, nextNumber: 2, nextDue: Today),
            Loan(healthyLoan, nextNumber: 5, nextDue: Today));
        var probe = new FakeSettlementHealthProbe();
        probe.Park(parkedLoan);
        var sink = new RecordingSink();
        var pass = NewPass(sink, new InstallmentRule(store, probe));

        // One pass over both loans: the parked instance is held, the healthy one fires — the gate keys
        // on the INSTANCE, never the whole pass.
        var fired = await pass.RunOnceAsync(Today);

        var decision = Assert.Single(fired);
        Assert.Equal(healthyLoan, decision.InstanceId);
        Assert.Equal(5, decision.OccurrenceKey);
    }

    [Fact]
    public async Task Installment_1_is_held_while_the_disbursement_leg_is_parked()
    {
        // The predicate is "the instance's cash leg is parked", which also covers the loan's OWN
        // disbursement movement: collecting installment 1 while the principal never actually left is the
        // same advance-past-collected-cash hazard, so the gate holds it too (strictly safer).
        var loan = Guid.NewGuid();
        var probe = new FakeSettlementHealthProbe();
        probe.Park(loan);
        var rule = new InstallmentRule(
            new FakeInstallmentStore(Loan(loan, nextNumber: 1, nextDue: Today)), probe);

        Assert.Empty(await rule.EvaluateAsync(Today));
    }

    // --- helpers (the InstallmentRuleTests shapes, kept local so each file reads standalone) ---

    private static readonly JsonStateSerializer<LoanPosition> Codec = new();
    private const string CollectionAccount = "acct-collect-001";

    private static LifecycleSchedulePass NewPass(ILifecycleCommandSink sink, params ILifecycleCommandRule[] rules) =>
        new(rules, new InMemoryLifecycleDispatchLedger(), sink);

    private static InstallmentCalendarReadModelRow Loan(Guid id, int nextNumber, DateOnly nextDue)
    {
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
