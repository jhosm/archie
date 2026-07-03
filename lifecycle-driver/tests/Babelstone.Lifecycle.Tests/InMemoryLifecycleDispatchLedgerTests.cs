using Babelstone.Engine.Hosting;
using Babelstone.Lifecycle;
using Xunit;

namespace Babelstone.Lifecycle.Tests;

/// <summary>
/// Tests for <see cref="LifecycleDispatchId"/> + <see cref="InMemoryLifecycleDispatchLedger"/> — the
/// number-pinned dispatch identity and the claim-port semantics the driver's single-firing rests on
/// (ADR-PC-036 §Decision 2; ADR-PC-038 §Decision 2+3; bd babelstone-1nkm.2), Docker-free over the
/// in-memory claim double:
/// <list type="bullet">
/// <item>the dispatch id is exactly the canonical, server-derived <c>LifecycleCommandKey.Derive</c> value —
/// the SAME id the engine derives, so the driver and the engine converge;</item>
/// <item>it is NUMBER-PINNED: a re-dated retry of the same occurrence (different due-date) yields the SAME
/// id, while a different occurrence NUMBER yields a different id;</item>
/// <item>an occurrence is claimable until recorded, and never again after — the claim consulted BEFORE
/// POSTing suppresses a re-tick without ever falsely skipping a not-yet-fired occurrence;</item>
/// <item>two concurrent claimants of the SAME occurrence resolve to exactly ONE winner (the in-process
/// mirror of the durable ledger's <c>FOR UPDATE SKIP LOCKED</c> single-firing claim);</item>
/// <item>releasing an UN-recorded claim (the failed/crashed POST) leaves the occurrence re-claimable —
/// claim-then-POST-then-record never strands a due command;</item>
/// <item>distinct occurrences are claimed independently.</item>
/// </list>
/// The durable Postgres twin of these semantics is asserted in
/// <c>LifecycleDispatchLedgerPostgresIntegrationTests</c> against the real table.
/// </summary>
public sealed class InMemoryLifecycleDispatchLedgerTests
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

        // The dispatch-ledger claim key IS the engine Idempotency-Key — the same value, derived the SAME
        // way the engine derives it (LCD-1, ADR-PC-036 §Decision 1+3; the ledger key per ADR-PC-038
        // §Decision 1), so a manual caller, the MCP agent and this driver converge on one dedupe receipt
        // per occurrence.
        Assert.Equal(
            LifecycleCommandKey.Derive(loan, PayInstallment, 1),
            LifecycleDispatchId.Of(decision));
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
        Assert.Equal(LifecycleDispatchId.Of(onJuly), LifecycleDispatchId.Of(reDated));

        // A different occurrence NUMBER is a genuinely different command — a different id.
        var occurrence2 = Installment(loan, number: 2, dueAt: new DateOnly(2026, 8, 1));
        Assert.NotEqual(LifecycleDispatchId.Of(onJuly), LifecycleDispatchId.Of(occurrence2));
    }

    [Fact]
    public async Task A_recorded_occurrence_is_never_claimable_again_even_re_dated()
    {
        var ledger = new InMemoryLifecycleDispatchLedger();
        var loan = Guid.NewGuid();

        // The forward calendar surfaces occurrence 1 again and again until the engine event lands — a
        // re-dated re-surfacing included. Before it is recorded the pass claims and fires it; once
        // recorded, every re-tick (even on a different due-date) gets no claim.
        var firstTick = Installment(loan, number: 1, dueAt: new DateOnly(2026, 7, 1));
        await using (var claim = await ledger.TryClaimAsync(firstTick))
        {
            Assert.NotNull(claim);
            Assert.Equal(LifecycleDispatchId.Of(firstTick), claim.DispatchId);
            await claim.RecordDispatchedAsync();
        }

        Assert.True(ledger.HasDispatched(firstTick));
        Assert.Null(await ledger.TryClaimAsync(firstTick));
        Assert.Null(await ledger.TryClaimAsync(Installment(loan, number: 1, dueAt: new DateOnly(2026, 9, 30))));
    }

    [Fact]
    public async Task Two_concurrent_claimants_of_the_same_occurrence_yield_exactly_one_winner()
    {
        // The single-firing shape (ADR-PC-038 §Decision 2) at the in-memory double: while a claim is HELD
        // (mid-POST), a competing claimant of the same occurrence gets null and skips it this tick —
        // exactly one replica fires. A DIFFERENT occurrence claims in parallel, unbothered.
        var ledger = new InMemoryLifecycleDispatchLedger();
        var loan = Guid.NewGuid();
        var occurrence1 = Installment(loan, number: 1, dueAt: new DateOnly(2026, 7, 1));

        await using var winner = await ledger.TryClaimAsync(occurrence1);
        Assert.NotNull(winner);
        Assert.Null(await ledger.TryClaimAsync(occurrence1));

        await using var other = await ledger.TryClaimAsync(Installment(loan, number: 2, dueAt: new DateOnly(2026, 8, 1)));
        Assert.NotNull(other);

        await winner.RecordDispatchedAsync();
        Assert.Null(await ledger.TryClaimAsync(occurrence1)); // recorded now — still exactly one firing.
    }

    [Fact]
    public async Task Releasing_an_unrecorded_claim_leaves_the_occurrence_re_claimable()
    {
        // The failed-POST path (ADR-PC-038 §Decision 3): the claim is a lease, not a record. Disposing it
        // un-recorded releases the occurrence, so the next pass retries — never reserve-before-POST,
        // which would strand a due command on a transient engine failure.
        var ledger = new InMemoryLifecycleDispatchLedger();
        var loan = Guid.NewGuid();
        var occurrence = Installment(loan, number: 1, dueAt: new DateOnly(2026, 7, 1));

        await using (var claim = await ledger.TryClaimAsync(occurrence))
        {
            Assert.NotNull(claim);
            // no RecordDispatchedAsync — the simulated POST failed.
        }

        Assert.False(ledger.HasDispatched(occurrence));
        await using var retry = await ledger.TryClaimAsync(occurrence);
        Assert.NotNull(retry);
    }

    [Fact]
    public async Task A_one_shot_maturity_uses_a_constant_occurrence_key()
    {
        var ledger = new InMemoryLifecycleDispatchLedger();
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

        await using (var claim = await ledger.TryClaimAsync(maturity))
        {
            Assert.NotNull(claim);
            await claim.RecordDispatchedAsync();
        }

        Assert.True(ledger.HasDispatched(maturity));
        Assert.Null(await ledger.TryClaimAsync(maturity));
    }
}
