using Babelstone.Engine.Hosting;
using Babelstone.Lifecycle;
using Xunit;

namespace Babelstone.Lifecycle.Tests;

/// <summary>
/// Tests for <see cref="LifecycleDispatchLedger"/> — the driver's "already fired this occurrence" memory
/// (ADR-PC-036 §Decision 2; bd babelstone-6cpq.7). They cover the acceptance criterion at the layer that owns
/// it: the ledger derives a composite dispatch id per occurrence and makes a re-tick of an already-dispatched
/// occurrence a no-op, with the number-pinned (LCD-1) idempotency the safety of a repeatable installment rests
/// on:
/// <list type="bullet">
/// <item>the dispatch id is exactly the canonical, server-derived <c>LifecycleCommandKey.Derive</c> value — the
/// SAME id the engine derives, so the driver and the engine converge;</item>
/// <item>it is NUMBER-PINNED: a re-dated retry of the same occurrence (different due-date) yields the SAME id,
/// while a different occurrence NUMBER yields a different id;</item>
/// <item><see cref="LifecycleDispatchLedger.HasDispatched"/> is false until recorded and true after — the
/// non-mutating check the pass consults BEFORE POSTing, so a re-tick is suppressed but a not-yet-fired
/// occurrence is never falsely skipped;</item>
/// <item>distinct occurrences are tracked independently.</item>
/// </list>
/// </summary>
public sealed class LifecycleDispatchLedgerTests
{
    private const string PayInstallment = "pay_installment";

    private static LifecycleCommandDecision Installment(Guid loan, long number, DateOnly dueAt) =>
        new(
            InstanceId: loan,
            CommandKind: PayInstallment,
            OccurrenceKey: number,
            RequestPath: $"/v1/loans/{loan:D}/installment",
            Body: new Dictionary<string, object?> { ["collection_account_ref"] = "acct-ref-001" },
            DueAt: dueAt);

    [Fact]
    public void The_dispatch_id_is_the_canonical_server_derived_lifecycle_command_key()
    {
        var loan = Guid.NewGuid();
        var decision = Installment(loan, number: 1, dueAt: new DateOnly(2026, 7, 1));

        // The dispatch-ledger key the driver dedupes on IS the engine Idempotency-Key — the same value, derived
        // the SAME way the engine derives it (LCD-1, ADR-PC-036 §Decision 1+3), so a manual caller, the MCP
        // agent and this driver converge on one dedupe receipt per occurrence.
        Assert.Equal(
            LifecycleCommandKey.Derive(loan, PayInstallment, 1),
            LifecycleDispatchLedger.DispatchId(decision));
    }

    [Fact]
    public void The_dispatch_id_is_number_pinned_not_date_pinned()
    {
        var loan = Guid.NewGuid();

        // The SAME occurrence NUMBER on two DIFFERENT due-dates (a re-dated or backfilled retry of occurrence 1)
        // must derive the SAME id — number-pinned, never date-pinned (ADR-PC-036 §Decision 3); that is what
        // dedupes a re-dated retry to one money leg (LIFECYCLE_COMMAND_NUMBER_PINNED_IDEMPOTENT).
        var onJuly = Installment(loan, number: 1, dueAt: new DateOnly(2026, 7, 1));
        var reDated = Installment(loan, number: 1, dueAt: new DateOnly(2026, 8, 15));
        Assert.Equal(LifecycleDispatchLedger.DispatchId(onJuly), LifecycleDispatchLedger.DispatchId(reDated));

        // A different occurrence NUMBER is a genuinely different command — a different id.
        var occurrence2 = Installment(loan, number: 2, dueAt: new DateOnly(2026, 8, 1));
        Assert.NotEqual(LifecycleDispatchLedger.DispatchId(onJuly), LifecycleDispatchLedger.DispatchId(occurrence2));
    }

    [Fact]
    public void A_re_tick_of_an_already_dispatched_occurrence_is_a_no_op()
    {
        var ledger = new LifecycleDispatchLedger();
        var loan = Guid.NewGuid();

        // The forward calendar surfaces occurrence 1 again and again until the engine event lands — a re-dated
        // re-surfacing included. Before it is recorded the pass would fire it; once recorded, every re-tick
        // (even on a different due-date) is suppressed.
        var firstTick = Installment(loan, number: 1, dueAt: new DateOnly(2026, 7, 1));
        Assert.False(ledger.HasDispatched(firstTick));

        ledger.RecordDispatched(firstTick);

        Assert.True(ledger.HasDispatched(firstTick));
        Assert.True(ledger.HasDispatched(Installment(loan, number: 1, dueAt: new DateOnly(2026, 9, 30))));
    }

    [Fact]
    public void Recording_is_idempotent_and_distinct_occurrences_are_independent()
    {
        var ledger = new LifecycleDispatchLedger();
        var loan = Guid.NewGuid();
        var occurrence1 = Installment(loan, number: 1, dueAt: new DateOnly(2026, 7, 1));
        var occurrence2 = Installment(loan, number: 2, dueAt: new DateOnly(2026, 8, 1));

        ledger.RecordDispatched(occurrence1);
        ledger.RecordDispatched(occurrence1); // recording the same id twice is a harmless no-op

        // Occurrence 1 is fired; occurrence 2 is still due — the next installment is not suppressed by the first.
        Assert.True(ledger.HasDispatched(occurrence1));
        Assert.False(ledger.HasDispatched(occurrence2));
    }

    [Fact]
    public void A_one_shot_maturity_uses_a_constant_occurrence_key()
    {
        var ledger = new LifecycleDispatchLedger();
        var deposit = Guid.NewGuid();

        // Deposit maturity is the degenerate single-occurrence case (ADR-PC-036 §S4): a constant occurrence key
        // (1). Firing it once suppresses every re-tick on the maturity calendar.
        var maturity = new LifecycleCommandDecision(
            InstanceId: deposit,
            CommandKind: "mature_deposit",
            OccurrenceKey: 1,
            RequestPath: $"/v1/deposits/{deposit:D}/maturity",
            Body: new Dictionary<string, object?>(),
            DueAt: new DateOnly(2026, 7, 1),
            ServicePrincipalScope: "lifecycle:deposit-money-mover");

        Assert.False(ledger.HasDispatched(maturity));
        ledger.RecordDispatched(maturity);
        Assert.True(ledger.HasDispatched(maturity));
    }
}
