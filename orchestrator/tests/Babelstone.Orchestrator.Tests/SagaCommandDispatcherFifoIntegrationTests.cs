using System.Collections.Concurrent;
using System.Net;
using Babelstone.Orchestrator.Dispatch;
using Babelstone.Families.TermDeposit.Orchestration;
using Babelstone.Orchestrator.Inbox;
using Babelstone.Orchestrator.Saga;
using Babelstone.TestFixtures;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace Babelstone.Orchestrator.Tests;

/// <summary>
/// In plain English: when a saga decides MORE THAN ONE command for the SAME deposit/aggregate and they
/// must run in a fixed order, the dispatcher must deliver them in that order and never two at once — but
/// commands for DIFFERENT aggregates should still go out in parallel. These tests prove the per-aggregate
/// FIFO guard (bd babelstone-t7o3.7): the dispatcher only ever has the EARLIEST still-PENDING command per
/// aggregate in flight, so a later command for one aggregate waits behind an earlier one that is stuck
/// (transient 5xx) or being delivered by another pod, while a different aggregate's command sails past.
/// </summary>
/// <remarks>
/// <para>
/// The guard has two layers, both exercised here:
/// <list type="bullet">
///   <item>The drain query reads at most ONE candidate per <c>process_id</c> — the earliest still-PENDING
///   <c>seq</c> (the per-aggregate FIFO head, served by <c>saga_outbox_pending_fifo_idx</c>, migration
///   0007). So a later command for an aggregate is never even SELECTed until the earlier one settles. This
///   is the in-process ordering guarantee proved by <see cref="A_later_command_for_one_aggregate_waits_behind_an_earlier_stuck_command"/>.</item>
///   <item>Each claim takes a per-<c>process_id</c> transaction advisory lock
///   (<c>pg_try_advisory_xact_lock</c>), so two dispatcher instances serialise on the SAME aggregate while
///   DIFFERENT aggregates run in parallel (the cross-instance guarantee). The lock is xact-scoped — it
///   releases on commit/rollback — so a transient failure frees the aggregate immediately for its retry.</item>
/// </list>
/// ADR-PC-029 slot 3: "per-aggregate ordering is the caller's responsibility." This hardens that from a
/// single-writer ASSUMPTION into an enforced GUARANTEE for any future saga that enqueues ordered commands.
/// </para>
/// <para>
/// A DEDICATED PostgreSQL container (NOT the shared collection fixture): the dispatcher's hosted loop
/// drains EVERY PENDING row in the database, so an isolated DB is the only way to assert exact ordering
/// without a sibling class's seeded rows leaking in (the same rationale as
/// <see cref="SagaCommandDispatcherIntegrationTests"/>). The engine/ACL is the minimal
/// <see cref="RecordingHttpServer"/> stand-in (the orchestrator subtree stays extraction-ready,
/// ADR-PC-019 §P2 — no engine-kernel reference even in tests).
/// </para>
/// </remarks>
[Trait("Category", "Integration")]
public sealed class SagaCommandDispatcherFifoIntegrationTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _pg = new PostgreSqlBuilder("postgres:18-alpine").Build();

    private string ConnectionString => _pg.GetConnectionString();

    public async Task InitializeAsync()
    {
        await _pg.GatedStartAsync();
        await new Migrations.MigrationRunner(ConnectionString).ApplyAsync();
    }

    public async Task DisposeAsync() => await _pg.DisposeAsync();

    [Fact]
    public async Task A_later_command_for_one_aggregate_waits_behind_an_earlier_stuck_command()
    {
        // One aggregate, TWO ordered commands: ReserveAccountBalance (seq N, the head) then ConfirmDebit
        // (seq N+1). They are order-dependent — the debit must never be delivered before its reservation.
        var processId = Guid.NewGuid();
        await StartSagaAsync(processId, correlationId: null);
        var reserveKey = await SeedCommandAsync(processId, ConstitutionProcess.ReserveAccountBalance);
        var debitKey = await SeedCommandAsync(processId, ConstitutionProcess.ConfirmDebit);

        // The settlement target holds the HEAD (ReserveAccountBalance) back with a transient 503 until the
        // test releases it. While the head is stuck PENDING, the FIFO guard must NEVER let the later
        // ConfirmDebit through: if the dispatcher ever POSTs the debit path, that is an out-of-order
        // delivery and the test fails.
        var releaseReserve = new ManualResetEventSlim(initialState: false);
        var debitDeliveredBeforeRelease = false;
        await using var settlement = new RecordingHttpServer(request =>
        {
            if (request.Path == "/v1/reservations" && request.IdempotencyKey == reserveKey.ToString())
            {
                // Hold the reservation transient until released; once released, accept it.
                return releaseReserve.IsSet
                    ? (HttpStatusCode.OK, "{}")
                    : (HttpStatusCode.ServiceUnavailable, "{}");
            }

            if (request.Path == "/v1/debits" && request.IdempotencyKey == debitKey.ToString())
            {
                // The debit reached the target. If the reservation has NOT been released yet, that is an
                // out-of-order delivery — the FIFO guard failed. Record it for the post-condition assert.
                if (!releaseReserve.IsSet)
                {
                    debitDeliveredBeforeRelease = true;
                }

                return (HttpStatusCode.OK, "{}");
            }

            return (HttpStatusCode.OK, "{}");
        });

        using var host = BuildHost(engineBaseUrl: "http://engine.invalid", settlementBaseUrl: settlement.BaseUrl);
        await host.StartAsync();
        try
        {
            // Let the dispatcher poll a number of cycles while the head is stuck. It must keep retrying the
            // reservation (head) and must NOT have delivered the debit yet (it is behind the FIFO head).
            await WaitUntilAsync(
                async () => settlement.Requests.Count(r => r.IdempotencyKey == reserveKey.ToString()) >= 2
                    && await StatusAsync(reserveKey) == "PENDING",
                TimeSpan.FromSeconds(30),
                "the dispatcher did not retry the stuck reservation head");

            // The hard invariant: while the head is stuck, the later command was never delivered and never
            // even claimed (still PENDING, never PUBLISHED).
            Assert.False(debitDeliveredBeforeRelease, "ConfirmDebit was delivered before its ReserveAccountBalance head settled — FIFO violated");
            Assert.DoesNotContain(settlement.Requests, r => r.IdempotencyKey == debitKey.ToString());
            Assert.Equal("PENDING", await StatusAsync(debitKey));

            // Release the head. Now the reservation settles, and ONLY THEN may the debit follow — in order.
            releaseReserve.Set();
            await WaitUntilAsync(
                async () => await StatusAsync(reserveKey) == "PUBLISHED" && await StatusAsync(debitKey) == "PUBLISHED",
                TimeSpan.FromSeconds(30),
                "the dispatcher did not deliver the reservation then the debit once the head was released");
        }
        finally
        {
            await host.StopAsync();
        }

        // Both delivered in order; the debit only ever reached the target AFTER the reservation was released.
        Assert.Equal("PUBLISHED", await StatusAsync(reserveKey));
        Assert.Equal("PUBLISHED", await StatusAsync(debitKey));
        Assert.False(debitDeliveredBeforeRelease, "ConfirmDebit was delivered before its ReserveAccountBalance head settled — FIFO violated");
    }

    [Fact]
    public async Task A_different_aggregate_dispatches_in_parallel_while_one_aggregate_is_stuck()
    {
        // Aggregate A's head is stuck on a transient 503; aggregate B is independent and must NOT be held
        // hostage to A — its command dispatches in parallel (the "different aggregates run in parallel"
        // half of the guard). Same command type on both so they share a path; told apart by message_id.
        var processA = Guid.NewGuid();
        var processB = Guid.NewGuid();
        await StartSagaAsync(processA, correlationId: null);
        await StartSagaAsync(processB, correlationId: null);
        var keyA = await SeedCommandAsync(processA, ConstitutionProcess.ReserveAccountBalance);
        var keyB = await SeedCommandAsync(processB, ConstitutionProcess.ReserveAccountBalance);

        var releaseA = new ManualResetEventSlim(initialState: false);
        await using var settlement = new RecordingHttpServer(request =>
        {
            if (request.IdempotencyKey == keyA.ToString())
            {
                // A's head is stuck transient until released.
                return releaseA.IsSet ? (HttpStatusCode.OK, "{}") : (HttpStatusCode.ServiceUnavailable, "{}");
            }

            // B (and anything else) succeeds immediately — it must not wait on A.
            return (HttpStatusCode.OK, "{}");
        });

        using var host = BuildHost(engineBaseUrl: "http://engine.invalid", settlementBaseUrl: settlement.BaseUrl);
        await host.StartAsync();
        try
        {
            // B settles WHILE A is still stuck PENDING — different aggregates dispatch in parallel.
            await WaitUntilAsync(
                async () => await StatusAsync(keyB) == "PUBLISHED" && await StatusAsync(keyA) == "PENDING",
                TimeSpan.FromSeconds(30),
                "aggregate B did not dispatch while aggregate A was stuck (parallel-across-aggregates failed)");

            Assert.Equal("PUBLISHED", await StatusAsync(keyB));
            Assert.Equal("PENDING", await StatusAsync(keyA));

            // Releasing A lets it settle too — neither aggregate starved the other.
            releaseA.Set();
            await WaitUntilAsync(
                async () => await StatusAsync(keyA) == "PUBLISHED",
                TimeSpan.FromSeconds(30),
                "aggregate A did not settle once released");
        }
        finally
        {
            await host.StopAsync();
        }

        Assert.Equal("PUBLISHED", await StatusAsync(keyA));
        Assert.Equal("PUBLISHED", await StatusAsync(keyB));
    }

    [Fact]
    public async Task Two_concurrent_dispatchers_never_deliver_a_pair_for_one_aggregate_out_of_order()
    {
        // The CROSS-INSTANCE guarantee: TWO dispatcher hosts drain the SAME database active-active. For one
        // aggregate with two ordered commands, the per-process advisory lock must keep the pair in order and
        // never concurrent — exactly one instance holds the aggregate at a time, and the FIFO head query
        // means only the reservation is ever in flight until it settles.
        var processId = Guid.NewGuid();
        await StartSagaAsync(processId, correlationId: null);
        var reserveKey = await SeedCommandAsync(processId, ConstitutionProcess.ReserveAccountBalance);
        var debitKey = await SeedCommandAsync(processId, ConstitutionProcess.ConfirmDebit);

        // Record the path-arrival order across BOTH dispatchers' requests for this aggregate's two keys.
        var arrivals = new ConcurrentQueue<string>();
        var reserved = 0;
        await using var settlement = new RecordingHttpServer(request =>
        {
            if (request.IdempotencyKey == reserveKey.ToString())
            {
                arrivals.Enqueue("reserve");
                Interlocked.Exchange(ref reserved, 1);
                return (HttpStatusCode.OK, "{}");
            }

            if (request.IdempotencyKey == debitKey.ToString())
            {
                arrivals.Enqueue("debit");
                // If the debit ever arrives before the reservation succeeded, ordering broke.
                Assert.Equal(1, Volatile.Read(ref reserved));
                return (HttpStatusCode.OK, "{}");
            }

            return (HttpStatusCode.OK, "{}");
        });

        // Two independent hosts against the one database — active-active.
        using var host1 = BuildHost(engineBaseUrl: "http://engine.invalid", settlementBaseUrl: settlement.BaseUrl);
        using var host2 = BuildHost(engineBaseUrl: "http://engine.invalid", settlementBaseUrl: settlement.BaseUrl);
        await host1.StartAsync();
        await host2.StartAsync();
        try
        {
            await WaitUntilAsync(
                async () => await StatusAsync(reserveKey) == "PUBLISHED" && await StatusAsync(debitKey) == "PUBLISHED",
                TimeSpan.FromSeconds(30),
                "the two dispatchers did not deliver both commands for the aggregate");
        }
        finally
        {
            await host1.StopAsync();
            await host2.StopAsync();
        }

        // Both delivered, and the FIRST arrival for this aggregate was the reservation, never the debit.
        var order = arrivals.ToArray();
        Assert.NotEmpty(order);
        Assert.Equal("reserve", order[0]);
        Assert.Equal("PUBLISHED", await StatusAsync(reserveKey));
        Assert.Equal("PUBLISHED", await StatusAsync(debitKey));
    }

    // ---- Host wiring (mirrors SagaCommandDispatcherIntegrationTests / the production composition) -------

    private IHost BuildHost(string engineBaseUrl, string settlementBaseUrl)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton(new SagaCommandDispatcherOptions
        {
            ConnectionString = ConnectionString,
            EngineBaseUrl = engineBaseUrl,
            SettlementBaseUrl = settlementBaseUrl,
            PollInterval = TimeSpan.FromMilliseconds(100),
        });
        builder.Services.AddSingleton<ICommandRouter, SagaCommandRouter>();
        builder.Services.AddHttpClient();
        AddSagaAdvanceHandler(builder.Services);
        builder.Services.AddSingleton<IResultEventBridge, ConstitutionResultEvents.Bridge>();
        builder.Services.AddSingleton(sp => new SagaCommandDispatchDrainer(
            sp.GetRequiredService<SagaCommandDispatcherOptions>(),
            sp.GetRequiredService<ICommandRouter>(),
            sp.GetRequiredService<IHttpClientFactory>(),
            sp.GetRequiredService<SagaAdvanceHandler>(),
            sp.GetServices<IResultEventBridge>()));
        builder.Services.AddHostedService<SagaCommandDispatcherService>();
        return builder.Build();
    }

    private static void AddSagaAdvanceHandler(IServiceCollection services)
    {
        services.AddSingleton<ISagaStateMachine, ConstitutionProcess>();
        services.AddSingleton<SagaStateStore>();
        services.AddSingleton<SagaTransitionLog>();
        services.AddSingleton<SagaBusinessReferenceStore>();
        services.AddSingleton<ISagaCommandSink>(sp =>
            new SagaCommandOutboxSink(sp.GetRequiredService<SagaBusinessReferenceStore>()));
        services.AddSingleton(sp => new SagaAdvanceHandler(
            sp.GetRequiredService<ISagaStateMachine>(),
            sp.GetRequiredService<SagaStateStore>(),
            sp.GetRequiredService<SagaTransitionLog>(),
            sp.GetRequiredService<ISagaCommandSink>()));
    }

    // ---- Seed helpers --------------------------------------------------------------------------

    private async Task StartSagaAsync(Guid processId, Guid? correlationId)
    {
        var stateStore = new SagaStateStore();
        var businessRefStore = new SagaBusinessReferenceStore();
        await using var connection = await OpenAsync();
        await using var tx = await connection.BeginTransactionAsync();
        await stateStore.TryStartAsync(
            connection, tx, processId, subjectId: processId, ConstitutionProcess.Type, ConstitutionProcess.States.Started, correlationId);
        await businessRefStore.TryInsertAsync(
            connection, tx,
            new SagaBusinessReference(
                ProcessId: processId,
                ProductRef: "TD-TRAD-12M",
                AmountMinorUnits: 100_00,
                SourceAccountRef: "acct-ref-001",
                InterestAccountRef: null,
                DepositRef: "DEP-" + processId.ToString("N"),
                ClientType: ClientType.Existing,
                AutoApprovalThresholdMinorUnits: 1_000_00));
        await tx.CommitAsync();
    }

    /// <summary>Emit one PENDING command row for the aggregate and return its freshly minted message_id
    /// (the Idempotency-Key). Multiple calls for one process build the ordered seq sequence the FIFO guard
    /// orders on — this returns the row most recently inserted, so each call's key is captured distinctly.</summary>
    private async Task<Guid> SeedCommandAsync(Guid processId, string commandType)
    {
        var sink = new SagaCommandOutboxSink();
        await using var connection = await OpenAsync();
        await using var tx = await connection.BeginTransactionAsync();
        await sink.EmitAsync(
            connection, tx, processId, commandType, causationMessageId: Guid.NewGuid(),
            correlationId: null, traceParent: null);
        await tx.CommitAsync();

        await using var read = await OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT message_id FROM saga_outbox WHERE process_id = @p AND command_type = @t ORDER BY seq DESC LIMIT 1;",
            read);
        command.Parameters.AddWithValue("p", processId);
        command.Parameters.AddWithValue("t", commandType);
        return (Guid)(await command.ExecuteScalarAsync())!;
    }

    private async Task<string> StatusAsync(Guid messageId)
    {
        await using var connection = await OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT status FROM saga_outbox WHERE message_id = @id;", connection);
        command.Parameters.AddWithValue("id", messageId);
        return (string)(await command.ExecuteScalarAsync())!;
    }

    private async Task<NpgsqlConnection> OpenAsync()
    {
        var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        return connection;
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition, TimeSpan timeout, string failureMessage)
    {
        var deadline = DateTime.UtcNow.Add(timeout);
        while (DateTime.UtcNow < deadline)
        {
            if (await condition())
            {
                return;
            }

            await Task.Delay(100);
        }

        throw new TimeoutException($"Timed out after {timeout.TotalSeconds}s: {failureMessage}.");
    }
}
