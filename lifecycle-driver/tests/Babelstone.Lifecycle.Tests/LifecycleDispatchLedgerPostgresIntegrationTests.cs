using Babelstone.Lifecycle;
using Npgsql;
using Xunit;

namespace Babelstone.Lifecycle.Tests;

/// <summary>
/// Integration tests for the durable dispatch ledger against a real PostgreSQL (ADR-PC-038; bd
/// babelstone-1nkm.2 + .3): the driver's own migration series applies, the per-occurrence atomic claim on
/// <c>lifecycle_dispatch_ledger</c> IS the multi-replica single-firing guard — the
/// <c>LIFECYCLE_DRIVER_SINGLE_FIRING</c> commitment (catalogue row LCD-4) — and the table's persistence is
/// the crash-survival + audit trail — <c>LIFECYCLE_DISPATCH_LEDGER_DURABLE</c> (row LCD-5). In plain
/// terms: N replicas all find the same due occurrence; exactly ONE wins the claim and POSTs it, a claim
/// that dies mid-POST is re-claimable, a host RESTART re-POSTs nothing (the durable row is the memory the
/// old in-memory HashSet lost on reboot), the <c>dispatched_at</c> column is the queryable "what did the
/// driver dispatch and when", and the engine's <c>command_dedup</c> (never this ledger) remains the
/// correctness floor for any re-POST. Same Testcontainers convention as the saga dispatcher's and outbox
/// relay's suites — the two precedents whose <c>FOR UPDATE SKIP LOCKED</c> pattern the ledger reuses.
/// </summary>
[Trait("Category", "Integration")]
[Collection(nameof(LifecyclePostgresCollection))]
public sealed class LifecycleDispatchLedgerPostgresIntegrationTests(LifecyclePostgresFixture fixture)
{
    private const string PayInstallment = "pay_installment";
    private static readonly DateOnly Today = new(2026, 7, 3);

    [Fact]
    public async Task Migration_creates_the_lifecycle_dispatch_ledger()
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT to_regclass('lifecycle_dispatch_ledger') IS NOT NULL;", connection);
        Assert.Equal(true, await command.ExecuteScalarAsync());
    }

    [Fact]
    public async Task LIFECYCLE_DRIVER_SINGLE_FIRING_concurrent_claimants_yield_exactly_one_winner()
    {
        // Eight concurrent claimants of the SAME due occurrence — the N-replica tick, compressed. The
        // atomic claim (FOR UPDATE SKIP LOCKED + the per-instance advisory lock) hands the row to exactly
        // one; the rest get null and skip it this tick (ADR-PC-038 §Decision 2 — no elected leader).
        var ledger = NewReplicaLedger();
        var decision = Installment(Guid.NewGuid(), number: 1, dueAt: Today);

        var claims = await Task.WhenAll(
            Enumerable.Range(0, 8).Select(_ => Task.Run(() => ledger.TryClaimAsync(decision))));

        var winners = claims.Where(c => c is not null).ToList();
        var winner = Assert.Single(winners);
        Assert.NotNull(winner);

        // The winner records (POST succeeded); afterwards NO claimant — this replica or any other — can
        // ever claim the occurrence again: the durable DISPATCHED row is the permanent no-op.
        await winner.RecordDispatchedAsync();
        await winner.DisposeAsync();
        Assert.Null(await ledger.TryClaimAsync(decision));
        Assert.Null(await NewReplicaLedger().TryClaimAsync(decision));
    }

    [Fact]
    public async Task LIFECYCLE_DRIVER_SINGLE_FIRING_two_replica_passes_post_a_due_occurrence_once()
    {
        // The full pass-level shape: TWO schedule passes (two replicas, each with its OWN ledger instance
        // over the SHARED database — exactly a two-pod deployment) run concurrently over the same due
        // occurrence. Whatever the interleaving — one claims first and commits, or holds the claim while
        // the other ticks — the shared sink sees exactly ONE POST (LCD-4).
        var loan = Guid.NewGuid();
        var decision = Installment(loan, number: 1, dueAt: Today);
        var sink = new CountingSink();
        var replicaA = new LifecycleSchedulePass([new FixedRule(decision)], NewReplicaLedger(), sink);
        var replicaB = new LifecycleSchedulePass([new FixedRule(decision)], NewReplicaLedger(), sink);

        var results = await Task.WhenAll(
            Task.Run(() => replicaA.RunOnceAsync(Today)),
            Task.Run(() => replicaB.RunOnceAsync(Today)));

        Assert.Equal(1, sink.Posts);
        Assert.Equal(1, results.Sum(r => r.Count));

        // The next tick on BOTH replicas is the durable no-op.
        Assert.Empty(await replicaA.RunOnceAsync(Today));
        Assert.Empty(await replicaB.RunOnceAsync(Today));
        Assert.Equal(1, sink.Posts);
    }

    [Fact]
    public async Task A_claim_that_dies_mid_post_is_re_claimable()
    {
        // The crash window (ADR-PC-038 §Decision 3): a replica claims, POSTs, and dies before the record
        // commits — modelled by disposing the claim UN-recorded (the rollback a dropped backend
        // connection effects). The occurrence stays PENDING and re-claimable, and the eventual re-POST is
        // deduped by the engine's command_dedup (ENGINE_COMMAND_IDEMPOTENT), never by this ledger.
        var ledger = NewReplicaLedger();
        var decision = Installment(Guid.NewGuid(), number: 1, dueAt: Today);

        var doomed = await ledger.TryClaimAsync(decision);
        Assert.NotNull(doomed);
        await doomed.DisposeAsync(); // crash: no RecordDispatchedAsync — the transaction rolls back.

        var retry = await NewReplicaLedger().TryClaimAsync(decision); // another replica picks it up.
        Assert.NotNull(retry);
        await retry.RecordDispatchedAsync();
        await retry.DisposeAsync();

        Assert.Null(await ledger.TryClaimAsync(decision));
    }

    [Fact]
    public async Task LIFECYCLE_DISPATCH_LEDGER_DURABLE_a_host_restart_does_not_re_post()
    {
        // The defect that motivated bd babelstone-1nkm.3: the old in-memory HashSet forgot everything on a
        // reboot, so a restarted host re-derived and re-POSTed every still-due occurrence. With the durable
        // ledger, a "restart" — a brand-new pass over a brand-new ledger instance, no shared process state,
        // only the shared database — claims nothing already dispatched (LCD-5, ADR-PC-038 §Decision 1).
        var loan = Guid.NewGuid();
        var decision = Installment(loan, number: 1, dueAt: Today);
        var sink = new CountingSink();

        var beforeRestart = new LifecycleSchedulePass([new FixedRule(decision)], NewReplicaLedger(), sink);
        Assert.Single(await beforeRestart.RunOnceAsync(Today));
        Assert.Equal(1, sink.Posts);

        // The reboot: nothing survives in process memory — the durable row is the whole memory.
        var afterRestart = new LifecycleSchedulePass([new FixedRule(decision)], NewReplicaLedger(), sink);
        Assert.Empty(await afterRestart.RunOnceAsync(Today));
        Assert.Equal(1, sink.Posts);
    }

    [Fact]
    public async Task LIFECYCLE_DISPATCH_LEDGER_DURABLE_records_a_dispatched_at_audit_trail()
    {
        // The queryable "what did the driver dispatch and when" (LCD-5): the DISPATCHED row carries the
        // id's three derivation parts denormalised, the business due date, and the DB-clock dispatched_at
        // stamp — structural references only, no PII (ADR-PC-004 §P2).
        var loan = Guid.NewGuid();
        var decision = Installment(loan, number: 7, dueAt: Today);
        var claim = await NewReplicaLedger().TryClaimAsync(decision);
        Assert.NotNull(claim);
        await claim.RecordDispatchedAsync();
        await claim.DisposeAsync();

        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        const string sql = """
            SELECT instance_id, command_kind, occurrence_key, due_at, status,
                   dispatched_at IS NOT NULL, first_seen_at IS NOT NULL
            FROM lifecycle_dispatch_ledger
            WHERE dispatch_id = @dispatch_id;
            """;
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("dispatch_id", LifecycleDispatchId.Of(decision));
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());

        Assert.Equal(loan, reader.GetGuid(0));
        Assert.Equal(PayInstallment, reader.GetString(1));
        Assert.Equal(7, reader.GetInt64(2));
        Assert.Equal(Today, reader.GetFieldValue<DateOnly>(3));
        Assert.Equal("DISPATCHED", reader.GetString(4));
        Assert.True(reader.GetBoolean(5)); // dispatched_at stamped (DB clock) — the audit column.
        Assert.True(reader.GetBoolean(6));
    }

    [Fact]
    public async Task Different_instances_claim_in_parallel_under_the_per_instance_lock()
    {
        // The per-instance advisory lock serialises replicas on the SAME instance; DIFFERENT instances
        // hash to different keys and proceed in parallel — hold one instance's claim open and claim
        // another instance's occurrence through the same ledger, unbothered.
        var ledger = NewReplicaLedger();
        var loanA = Installment(Guid.NewGuid(), number: 1, dueAt: Today);
        var loanB = Installment(Guid.NewGuid(), number: 1, dueAt: Today);

        var heldA = await ledger.TryClaimAsync(loanA);
        Assert.NotNull(heldA);

        var heldB = await NewReplicaLedger().TryClaimAsync(loanB);
        Assert.NotNull(heldB);

        await heldA.RecordDispatchedAsync();
        await heldA.DisposeAsync();
        await heldB.RecordDispatchedAsync();
        await heldB.DisposeAsync();
    }

    // --- helpers ---

    /// <summary>A fresh ledger instance over the shared database — one "replica" (each pod constructs its
    /// own <see cref="PostgresLifecycleDispatchLedger"/>; the coordination lives in Postgres, not the
    /// object).</summary>
    private PostgresLifecycleDispatchLedger NewReplicaLedger() => new(fixture.ConnectionString);

    private static LifecycleCommandDecision Installment(Guid loan, long number, DateOnly dueAt) =>
        new(
            InstanceId: loan,
            CommandKind: PayInstallment,
            OccurrenceKey: number,
            RequestPath: $"/v1/loans/{loan:D}/installment",
            Body: new Dictionary<string, object?> { ["collection_account_ref"] = "acct-ref-001" },
            DueAt: dueAt);

    /// <summary>A rule pinned to one due decision — the same occurrence surfacing on every replica's
    /// forward-calendar read.</summary>
    private sealed class FixedRule(LifecycleCommandDecision decision) : ILifecycleCommandRule
    {
        public string FamilyName => "fake";

        public Task<IReadOnlyList<LifecycleCommandDecision>> EvaluateAsync(
            DateOnly asOf, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<LifecycleCommandDecision>>([decision]);
    }

    /// <summary>Counts POSTs across concurrently running passes (thread-safe).</summary>
    private sealed class CountingSink : ILifecycleCommandSink
    {
        private int _posts;

        public int Posts => Volatile.Read(ref _posts);

        public Task DispatchAsync(
            LifecycleCommandDecision decision, Guid commandId, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _posts);
            return Task.CompletedTask;
        }
    }
}
