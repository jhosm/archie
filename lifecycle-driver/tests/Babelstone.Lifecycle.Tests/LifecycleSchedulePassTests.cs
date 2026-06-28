using Babelstone.Engine.Hosting;
using Babelstone.Lifecycle;
using Xunit;

namespace Babelstone.Lifecycle.Tests;

/// <summary>
/// Tests for <see cref="LifecycleSchedulePass"/> — the driver's per-tick engine (ADR-PC-036 §Decision 2; bd
/// babelstone-6cpq.7). They cover the idempotency + ordering the driver rests on, over a fake rule and a
/// recording sink (no live engine):
/// <list type="bullet">
/// <item>running a pass TWICE over the same due occurrence POSTs it ONCE — the dispatch ledger absorbs the
/// re-tick (the calendar keeps surfacing it until the engine event lands);</item>
/// <item>a genuinely new occurrence on a later pass is still POSTed (the dedupe is keyed, not a blunt
/// "already ran" flag);</item>
/// <item>the pass is family-agnostic — it dispatches whatever each registered <see cref="ILifecycleCommandRule"/>
/// returns, driven by fake rules that the pass never names;</item>
/// <item>the POSTed command id is exactly the canonical <c>LifecycleCommandKey.Derive</c> value (LCD-1);</item>
/// <item>a sink failure leaves the occurrence UN-recorded — the next pass retries it (check-then-POST-then-record
/// ordering), so a transient engine outage never strands a due command.</item>
/// </list>
/// The as-of date is an INPUT (no clock read inside the pass), and the real <see cref="LifecycleDispatchLedger"/>
/// backs the dedupe.
/// </summary>
public sealed class LifecycleSchedulePassTests
{
    private const string PayInstallment = "pay_installment";
    private static readonly DateOnly Today = new(2026, 6, 28);

    [Fact]
    public async Task Running_the_pass_twice_over_the_same_occurrence_posts_it_once()
    {
        var loan = Guid.NewGuid();
        var sink = new RecordingSink();
        var pass = NewPass(sink, new FakeRule(_ => [Installment(loan, 1, Today.AddDays(3))]));

        var first = await pass.RunOnceAsync(Today);
        var second = await pass.RunOnceAsync(Today);

        Assert.Single(first);
        Assert.Empty(second);

        // POSTed exactly once, under the canonical server-derived number-pinned key (LCD-1).
        Assert.Single(sink.Dispatched);
        Assert.Equal(LifecycleCommandKey.Derive(loan, PayInstallment, 1), first[0].CommandId);
        Assert.Equal(first[0].CommandId, sink.Dispatched[0].CommandId);
        Assert.Equal(loan, first[0].InstanceId);
    }

    [Fact]
    public async Task A_genuinely_new_occurrence_on_a_later_pass_is_still_posted()
    {
        var loan = Guid.NewGuid();
        var calendar = new List<LifecycleCommandDecision> { Installment(loan, 1, Today.AddDays(2)) };
        var sink = new RecordingSink();
        var pass = NewPass(sink, new FakeRule(_ => calendar.ToArray()));

        var pass1 = await pass.RunOnceAsync(Today);
        // Occurrence 1 has been paid (the engine event landed) and occurrence 2 is now the next due one.
        calendar.Clear();
        calendar.Add(Installment(loan, 2, Today.AddDays(32)));
        var pass2 = await pass.RunOnceAsync(Today);

        Assert.Single(pass1);
        Assert.Equal(1, pass1[0].OccurrenceKey);
        Assert.Single(pass2);
        Assert.Equal(2, pass2[0].OccurrenceKey);
        Assert.Equal(2, sink.Dispatched.Count);
    }

    [Fact]
    public async Task The_pass_enumerates_every_registered_rule()
    {
        // Family-agnostic by construction: the pass dispatches the decisions of ALL registered rules, so a
        // second family's rule contributes alongside the first with zero core diff (ADR-PC-036 §S4 — a third
        // lifecycle is a new rule, no core change).
        var loan = Guid.NewGuid();
        var deposit = Guid.NewGuid();
        var sink = new RecordingSink();
        var pass = NewPass(
            sink,
            new FakeRule(_ => [Installment(loan, 1, Today.AddDays(1))]),
            new FakeRule(_ => [Maturity(deposit, Today.AddDays(2))]));

        var dispatched = await pass.RunOnceAsync(Today);

        Assert.Equal(2, dispatched.Count);
        Assert.Contains(dispatched, d => d.InstanceId == loan && d.CommandKind == PayInstallment);
        Assert.Contains(dispatched, d => d.InstanceId == deposit && d.CommandKind == "mature_deposit");
    }

    [Fact]
    public async Task A_sink_failure_leaves_the_occurrence_unrecorded_so_the_next_pass_retries()
    {
        var loan = Guid.NewGuid();
        var sink = new FlakySink(throwUntilAttempt: 1);
        var pass = NewPass(sink, new FakeRule(_ => [Installment(loan, 1, Today.AddDays(3))]));

        // The first pass's POST fails — it propagates (the worker would treat it as backpressure) and the
        // occurrence is NOT recorded dispatched.
        await Assert.ThrowsAsync<InvalidOperationException>(() => pass.RunOnceAsync(Today));
        Assert.Equal(1, sink.Attempts);

        // The next pass re-derives the still-due occurrence and POSTs it again — the engine's command_dedup
        // makes that retry safe (ADR-PC-029 slot 4). Check-then-POST-then-record never strands a due command.
        var retry = await pass.RunOnceAsync(Today);
        Assert.Single(retry);
        Assert.Equal(2, sink.Attempts);

        // And now it IS recorded — a subsequent pass is the no-op re-tick.
        var third = await pass.RunOnceAsync(Today);
        Assert.Empty(third);
    }

    // --- helpers ---

    private static LifecycleSchedulePass NewPass(ILifecycleCommandSink sink, params ILifecycleCommandRule[] rules) =>
        new(rules, new LifecycleDispatchLedger(), sink);

    private static LifecycleCommandDecision Installment(Guid loan, long number, DateOnly dueAt) =>
        new(
            InstanceId: loan,
            CommandKind: PayInstallment,
            OccurrenceKey: number,
            RequestPath: $"/v1/loans/{loan:D}/installment",
            Body: new Dictionary<string, object?> { ["collection_account_ref"] = "acct-ref-001" },
            DueAt: dueAt);

    private static LifecycleCommandDecision Maturity(Guid deposit, DateOnly dueAt) =>
        new(
            InstanceId: deposit,
            CommandKind: "mature_deposit",
            OccurrenceKey: 1,
            RequestPath: $"/v1/deposits/{deposit:D}/maturity",
            Body: new Dictionary<string, object?>(),
            DueAt: dueAt,
            ServicePrincipalScope: "lifecycle:deposit-money-mover");

    /// <summary>A rule that returns whatever the supplied function produces for the as-of date — names no
    /// family the pass depends on.</summary>
    private sealed class FakeRule(Func<DateOnly, LifecycleCommandDecision[]> evaluate) : ILifecycleCommandRule
    {
        public string FamilyName => "fake";

        public Task<IReadOnlyList<LifecycleCommandDecision>> EvaluateAsync(DateOnly asOf, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<LifecycleCommandDecision>>(evaluate(asOf));
    }

    /// <summary>Records every command the pass POSTs through it.</summary>
    private sealed class RecordingSink : ILifecycleCommandSink
    {
        private readonly List<DispatchedCommand> _dispatched = [];

        public IReadOnlyList<DispatchedCommand> Dispatched => _dispatched;

        public Task DispatchAsync(LifecycleCommandDecision decision, Guid commandId, CancellationToken ct = default)
        {
            _dispatched.Add(new DispatchedCommand(
                commandId, decision.InstanceId, decision.CommandKind, decision.OccurrenceKey,
                decision.RequestPath, decision.DueAt));
            return Task.CompletedTask;
        }
    }

    /// <summary>Throws on its first <c>throwUntilAttempt</c> POST(s) then succeeds — to prove a sink failure
    /// leaves the occurrence un-recorded and the next pass retries it.</summary>
    private sealed class FlakySink(int throwUntilAttempt) : ILifecycleCommandSink
    {
        public int Attempts { get; private set; }

        public Task DispatchAsync(LifecycleCommandDecision decision, Guid commandId, CancellationToken ct = default)
        {
            Attempts++;
            if (Attempts <= throwUntilAttempt)
            {
                throw new InvalidOperationException("simulated engine backpressure (5xx/timeout)");
            }

            return Task.CompletedTask;
        }
    }
}
